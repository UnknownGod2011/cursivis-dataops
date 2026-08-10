"""Seed the deterministic Cursivis DataOps demo catalog into local DataHub.

This script intentionally uses DataHub's public Python SDK rather than tracked
example JSON. It creates the exact dataset referenced by the golden SQL demo,
adds an owner and downstream lineage, and verifies that DataHub can read the
metadata back before returning success.
"""

from __future__ import annotations

import json
import os
import sys
import time
import urllib.error
import urllib.request

from datahub.metadata.urns import CorpUserUrn, DatasetUrn
from datahub.sdk import DataHubClient, Dataset

GMS_URL = os.getenv("DATAHUB_GMS_URL", "http://localhost:8080").rstrip("/")
GRAPHQL_URL = os.getenv("DATAHUB_GRAPHQL_URL", f"{GMS_URL}/api/graphql")
TOKEN = os.getenv("DATAHUB_TOKEN") or None
PLATFORM = "postgres"
ENV = "PROD"

DATASETS = {
    "raw.customers": [
        ("customer_id", "varchar(64)", "Stable customer identifier from the source CRM."),
        ("lifetime_value_usd", "decimal(18,2)", "Lifetime customer value normalized to USD."),
        ("customer_tier", "varchar(32)", "Governed customer segment such as enterprise, growth, or standard."),
        ("updated_at", "timestamp", "Last source update timestamp."),
    ],
    "analytics.customers": [
        ("customer_id", "varchar(64)", "Stable customer identifier used across analytics products."),
        ("lifetime_value_usd", "decimal(18,2)", "Authoritative lifetime value metric in USD."),
        ("customer_tier", "varchar(32)", "Authoritative governed customer tier."),
        ("updated_at", "timestamp", "Last successful analytics refresh timestamp."),
    ],
    "analytics.executive_revenue": [
        ("customer_id", "varchar(64)", "Customer identifier used for executive revenue aggregation."),
        ("lifetime_value_usd", "decimal(18,2)", "Customer lifetime value consumed by executive reporting."),
    ],
    "ml.churn_prediction_features": [
        ("customer_id", "varchar(64)", "Customer identifier used by the churn feature pipeline."),
        ("customer_tier", "varchar(32)", "Customer tier used as a churn model feature."),
        ("lifetime_value_usd", "decimal(18,2)", "Customer lifetime value used as a churn model feature."),
    ],
}


def graphql(query: str, variables: dict) -> dict:
    body = json.dumps({"query": query, "variables": variables}).encode("utf-8")
    headers = {"Content-Type": "application/json"}
    if TOKEN:
        headers["Authorization"] = f"Bearer {TOKEN}"
    request = urllib.request.Request(GRAPHQL_URL, data=body, headers=headers, method="POST")
    with urllib.request.urlopen(request, timeout=15) as response:
        payload = json.loads(response.read().decode("utf-8"))
    if payload.get("errors"):
        raise RuntimeError(f"DataHub GraphQL returned errors: {payload['errors']}")
    return payload.get("data") or {}


def dataset_urn(name: str) -> DatasetUrn:
    return DatasetUrn(platform=PLATFORM, name=name, env=ENV)


def seed() -> None:
    client = DataHubClient(server=GMS_URL, token=TOKEN)

    for name, schema in DATASETS.items():
        client.entities.upsert(Dataset(platform=PLATFORM, name=name, schema=schema))
        print(f"Seeded dataset: {name}")

    target_urn = dataset_urn("analytics.customers")
    target = client.entities.get(target_urn)
    target.add_owner(CorpUserUrn("datahub"))
    client.entities.update(target)
    print("Attached technical demo owner: urn:li:corpuser:datahub")

    client.lineage.add_lineage(
        upstream=dataset_urn("raw.customers"),
        downstream=target_urn,
        column_lineage="auto_strict",
    )
    client.lineage.add_lineage(
        upstream=target_urn,
        downstream=dataset_urn("analytics.executive_revenue"),
        column_lineage="auto_strict",
    )
    client.lineage.add_lineage(
        upstream=target_urn,
        downstream=dataset_urn("ml.churn_prediction_features"),
        column_lineage="auto_strict",
    )
    print("Seeded upstream and downstream lineage.")


def verify() -> None:
    target = str(dataset_urn("analytics.customers"))
    dataset_query = """
    query Dataset($urn: String!) {
      dataset(urn: $urn) {
        urn
        schemaMetadata { fields { fieldPath nativeDataType description } }
        ownership { owners { owner { urn } } }
      }
    }
    """
    lineage_query = """
    query Lineage($input: LineageInput!) {
      lineage(input: $input) { relationships { entity { urn type } } }
    }
    """
    search_query = """
    query Search($input: SearchInput!) {
      search(input: $input) { searchResults { entity { urn type } } }
    }
    """

    data = graphql(dataset_query, {"urn": target})
    entity = data.get("dataset")
    if not entity:
        raise RuntimeError("Verification failed: analytics.customers was not readable from DataHub.")

    fields = {
        field.get("fieldPath")
        for field in ((entity.get("schemaMetadata") or {}).get("fields") or [])
    }
    required_fields = {"customer_id", "lifetime_value_usd", "customer_tier", "updated_at"}
    if not required_fields.issubset(fields):
        raise RuntimeError(f"Verification failed: schema fields missing. Found: {sorted(fields)}")

    owners = {
        ((item.get("owner") or {}).get("urn"))
        for item in ((entity.get("ownership") or {}).get("owners") or [])
    }
    if "urn:li:corpuser:datahub" not in owners:
        raise RuntimeError(f"Verification failed: expected owner missing. Found: {sorted(x for x in owners if x)}")

    downstream = graphql(
        lineage_query,
        {"input": {"urn": target, "direction": "DOWNSTREAM", "start": 0, "count": 20}},
    )
    downstream_urns = {
        ((item.get("entity") or {}).get("urn"))
        for item in (((downstream.get("lineage") or {}).get("relationships")) or [])
    }
    expected_downstream = {
        str(dataset_urn("analytics.executive_revenue")),
        str(dataset_urn("ml.churn_prediction_features")),
    }
    if not expected_downstream.issubset(downstream_urns):
        raise RuntimeError(
            "Verification failed: downstream lineage missing. "
            f"Found: {sorted(x for x in downstream_urns if x)}"
        )

    # Search is eventually consistent. Wait until the same lookup Cursivis uses
    # can resolve the canonical dataset before declaring the demo ready.
    resolved = False
    for _ in range(20):
        search = graphql(
            search_query,
            {"input": {"type": "DATASET", "query": "analytics.customers", "start": 0, "count": 5}},
        )
        results = ((search.get("search") or {}).get("searchResults")) or []
        if any(((item.get("entity") or {}).get("urn")) == target for item in results):
            resolved = True
            break
        time.sleep(1)
    if not resolved:
        raise RuntimeError("Verification failed: DataHub search did not index analytics.customers in time.")

    print("Verified canonical dataset, schema, owner, downstream lineage, and search resolution.")


def main() -> int:
    try:
        seed()
        verify()
        return 0
    except (RuntimeError, urllib.error.URLError, TimeoutError) as exc:
        print(f"Demo seed failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
