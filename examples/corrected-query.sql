SELECT customer_id, lifetime_value_usd
FROM analytics.customers
WHERE customer_tier = 'enterprise';
