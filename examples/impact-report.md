# Impact report: `analytics.customer_360`

`lifetime_value` and `tier` are not fields on the authoritative customer model. The compatible fields are `lifetime_value_usd` and `customer_tier`.

The asset is owned by `data-platform`; known downstream consumers include `dashboard.executive_revenue` and `model.churn_prediction`. Validate semantic changes with that owner before altering the customer-value definition.
