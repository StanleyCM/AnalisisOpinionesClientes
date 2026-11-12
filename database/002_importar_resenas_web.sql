-- Definir la variable de entorno RutaCsvResenas y ejecutar con sqlcmd -b -d AnalisisOpinionesETL.
-- La ruta debe ser accesible para SQL Server. En la variable SQLCMD, duplicar las comillas simples.
IF DB_NAME() <> N'AnalisisOpinionesETL'
    THROW 50000, 'Seleccione la base AnalisisOpinionesETL.', 1;

SET NOCOUNT ON;
SET XACT_ABORT ON;

CREATE TABLE #ResenaWebCsv (
    ResenaId NVARCHAR(60) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY,
    ClienteIdOriginal NVARCHAR(60) NULL,
    ProductoIdOriginal NVARCHAR(60) NULL,
    Fecha DATE NULL,
    Comentario NVARCHAR(MAX) NULL,
    Rating TINYINT NULL,
    CHECK (LEN(ResenaId) > 0),
    CHECK (Rating BETWEEN 1 AND 5)
);

BEGIN TRY
    BEGIN TRANSACTION;

    BULK INSERT #ResenaWebCsv FROM '$(RutaCsvResenas)'
    WITH (
        FORMAT = 'CSV', CODEPAGE = '65001', FIRSTROW = 2, ROWTERMINATOR = '0x0a',
        CHECK_CONSTRAINTS, KEEPNULLS, MAXERRORS = 0
    );

    INSERT INTO origen.ResenaWeb (ResenaId, ClienteIdOriginal, ProductoIdOriginal, Fecha, Comentario, Rating)
    SELECT csv.ResenaId, csv.ClienteIdOriginal, csv.ProductoIdOriginal, csv.Fecha, csv.Comentario, csv.Rating
    FROM #ResenaWebCsv AS csv
    WHERE NOT EXISTS (
        SELECT 1 FROM origen.ResenaWeb AS existente WITH (UPDLOCK, HOLDLOCK)
        WHERE existente.ResenaId = csv.ResenaId
    );

    DECLARE @insertadas INT = @@ROWCOUNT;
    COMMIT TRANSACTION;

    SELECT (SELECT COUNT(*) FROM #ResenaWebCsv) AS FilasCsv,
           @insertadas AS Insertadas,
           (SELECT COUNT(*) FROM origen.ResenaWeb AS resena
            INNER JOIN #ResenaWebCsv AS csv ON csv.ResenaId = resena.ResenaId) AS Disponibles;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
