# Resolution: customer model field mismatch

Approved finding: queries should read `analytics.customer_360`, use `customer_tier` for segmentation, and use `lifetime_value_usd` as the monetary lifetime-value field. The earlier `analytics.customers` reference is not the governed customer-360 asset.

This is the exact reviewed text offered to the user for explicit DataHub write-back; Cursivis never overwrites catalog metadata automatically.
