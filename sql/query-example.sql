-- Esempio per --query-file: stesse colonne e ordine, parametri @from e @to.
SELECT StockDate, WarehouseCode, Sku, Category, OnHand, Inbound, Outbound, Adjustment, UnitCost
FROM dbo.StockDaily
WHERE StockDate BETWEEN @from AND @to
  AND WarehouseCode IN (N'MI01', N'RM02')
ORDER BY WarehouseCode, Sku, StockDate;
