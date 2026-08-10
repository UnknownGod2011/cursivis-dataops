SELECT customer_id, lifetime_value_usd
FROM analytics.customer_360
WHERE customer_tier = 'enterprise';
