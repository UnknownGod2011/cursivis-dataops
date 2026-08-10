# Impact report: `analytics.customers`

`lifetime_value` and `tier` are not fields on the authoritative customer dataset. DataHub shows the governed fields are `lifetime_value_usd` and `customer_tier`.

The dataset is owned by `urn:li:corpuser:datahub` in the deterministic local demo. Its upstream source is `raw.customers`; known downstream consumers include `analytics.executive_revenue` and `ml.churn_prediction_features`.

That lineage is why Cursivis DataOps can warn about blast radius instead of treating the selected SQL as an isolated snippet.
