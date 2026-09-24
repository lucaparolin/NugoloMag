-- Tabella dei fatti giornalieri letta da NugoloMag Analyst (SQL Server).
-- In alternativa, creare una VISTA con queste colonne sopra le tabelle del gestionale
-- oppure passare una query propria con --query-file (parametri @from e @to).
CREATE TABLE dbo.StockDaily
(
    StockDate     date           NOT NULL,
    WarehouseCode nvarchar(20)   NOT NULL,
    Sku           nvarchar(50)   NOT NULL,
    Category      nvarchar(100)  NULL,
    OnHand        decimal(18, 3) NOT NULL,  -- giacenza a fine giornata
    Inbound       decimal(18, 3) NOT NULL,  -- entrate del giorno
    Outbound      decimal(18, 3) NOT NULL,  -- uscite del giorno
    Adjustment    decimal(18, 3) NOT NULL,  -- rettifiche inventariali (+/-)
    UnitCost      decimal(18, 4) NULL,
    CONSTRAINT PK_StockDaily PRIMARY KEY (StockDate, WarehouseCode, Sku)
);

CREATE INDEX IX_StockDaily_Warehouse ON dbo.StockDaily (WarehouseCode, Sku, StockDate)
    INCLUDE (Category, OnHand, Inbound, Outbound, Adjustment, UnitCost);
GO

-- Esempio di vista da un gestionale con movimenti e saldi separati (da adattare ai nomi reali).
-- CREATE VIEW dbo.StockDaily AS
-- SELECT s.Data AS StockDate, s.CodMag AS WarehouseCode, s.CodArt AS Sku, a.Famiglia AS Category,
--        s.Giacenza AS OnHand,
--        ISNULL(m.Carichi, 0) AS Inbound, ISNULL(m.Scarichi, 0) AS Outbound, ISNULL(m.Rettifiche, 0) AS Adjustment,
--        a.CostoStd AS UnitCost
-- FROM dbo.SaldiGiornalieri s
-- JOIN dbo.Articoli a ON a.CodArt = s.CodArt
-- LEFT JOIN dbo.MovimentiGiornalieri m ON m.Data = s.Data AND m.CodMag = s.CodMag AND m.CodArt = s.CodArt;
