-- Ejecutar en una base AnalisisOpinionesETL existente.
IF DB_NAME() <> N'AnalisisOpinionesETL'
    THROW 50000, 'Seleccione la base AnalisisOpinionesETL.', 1;

SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF SCHEMA_ID(N'config') IS NULL
        EXEC(N'CREATE SCHEMA config AUTHORIZATION dbo;');
    IF SCHEMA_ID(N'origen') IS NULL
        EXEC(N'CREATE SCHEMA origen AUTHORIZATION dbo;');
    IF SCHEMA_ID(N'staging') IS NULL
        EXEC(N'CREATE SCHEMA staging AUTHORIZATION dbo;');
    IF SCHEMA_ID(N'dw') IS NULL
        EXEC(N'CREATE SCHEMA dw AUTHORIZATION dbo;');

    IF OBJECT_ID(N'config.FuenteDatos', N'U') IS NULL
    BEGIN
        CREATE TABLE config.FuenteDatos (
            FuenteId INT IDENTITY(1,1) CONSTRAINT PK_FuenteDatos PRIMARY KEY,
            Codigo NVARCHAR(50) NOT NULL CONSTRAINT UQ_FuenteDatos_Codigo UNIQUE,
            Nombre NVARCHAR(150) NOT NULL,
            Tipo NVARCHAR(20) NOT NULL,
            Activa BIT NOT NULL CONSTRAINT DF_Fuente_Activa DEFAULT (1),
            FechaCreacionUtc DATETIME2 NOT NULL CONSTRAINT DF_Fuente_Fecha DEFAULT SYSUTCDATETIME(),
            CONSTRAINT CK_Fuente_Tipo CHECK (Tipo IN (N'CSV', N'SQL', N'API'))
        );
    END;

    IF OBJECT_ID(N'origen.ResenaWeb', N'U') IS NULL
    BEGIN
        CREATE TABLE origen.ResenaWeb (
            ResenaId NVARCHAR(60) CONSTRAINT PK_ResenaWeb PRIMARY KEY,
            ClienteIdOriginal NVARCHAR(60) NULL,
            ProductoIdOriginal NVARCHAR(60) NULL,
            Fecha DATE NULL,
            Comentario NVARCHAR(MAX) NULL,
            Rating TINYINT NULL,
            CONSTRAINT CK_Resena_Rating CHECK (Rating BETWEEN 1 AND 5)
        );
        CREATE INDEX IX_ResenaWeb_Fecha ON origen.ResenaWeb(Fecha);
    END;

    IF OBJECT_ID(N'staging.Ejecucion', N'U') IS NULL
    BEGIN
        CREATE TABLE staging.Ejecucion (
            EjecucionId BIGINT IDENTITY(1,1) CONSTRAINT PK_Ejecucion PRIMARY KEY,
            FechaInicioUtc DATETIME2 NOT NULL CONSTRAINT DF_Ejecucion_Inicio DEFAULT SYSUTCDATETIME(),
            FechaFinUtc DATETIME2 NULL,
            Estado NVARCHAR(30) NOT NULL CONSTRAINT DF_Ejecucion_Estado DEFAULT N'Iniciada',
            RegistrosLeidos INT NOT NULL CONSTRAINT DF_Ejecucion_Leidos DEFAULT (0),
            RegistrosGuardados INT NOT NULL CONSTRAINT DF_Ejecucion_Guardados DEFAULT (0),
            RegistrosConError INT NOT NULL CONSTRAINT DF_Ejecucion_Errores DEFAULT (0),
            MensajeError NVARCHAR(MAX) NULL
        );
    END;

    IF OBJECT_ID(N'staging.RegistroExtraido', N'U') IS NULL
    BEGIN
        CREATE TABLE staging.RegistroExtraido (
            RegistroId BIGINT IDENTITY(1,1) CONSTRAINT PK_RegistroExtraido PRIMARY KEY,
            EjecucionId BIGINT NOT NULL,
            FuenteId INT NOT NULL,
            RegistroExternoId NVARCHAR(100) NULL,
            ClienteExternoId NVARCHAR(100) NULL,
            ProductoExternoId NVARCHAR(100) NULL,
            FechaOriginal NVARCHAR(50) NULL,
            ComentarioOriginal NVARCHAR(MAX) NULL,
            PuntajeOriginal NVARCHAR(50) NULL,
            ClasificacionOriginal NVARCHAR(50) NULL,
            CanalOriginal NVARCHAR(100) NULL,
            DatosOriginalesJson NVARCHAR(MAX) NOT NULL,
            FechaExtraccionUtc DATETIME2 NOT NULL CONSTRAINT DF_Registro_Fecha DEFAULT SYSUTCDATETIME(),
            CONSTRAINT CK_Registro_Json CHECK (ISJSON(DatosOriginalesJson) = 1),
            CONSTRAINT FK_Registro_Ejecucion FOREIGN KEY (EjecucionId) REFERENCES staging.Ejecucion(EjecucionId),
            CONSTRAINT FK_Registro_Fuente FOREIGN KEY (FuenteId) REFERENCES config.FuenteDatos(FuenteId)
        );
        CREATE INDEX IX_Registro_Ejecucion ON staging.RegistroExtraido(EjecucionId);
        CREATE INDEX IX_Registro_Fuente ON staging.RegistroExtraido(FuenteId);
    END;

    IF OBJECT_ID(N'dw.DimFecha', N'U') IS NULL
    BEGIN
        CREATE TABLE dw.DimFecha (
            FechaKey INT CONSTRAINT PK_DimFecha PRIMARY KEY,
            Fecha DATE NOT NULL CONSTRAINT UQ_DimFecha_Fecha UNIQUE,
            Dia TINYINT NOT NULL,
            Mes TINYINT NOT NULL,
            Anio SMALLINT NOT NULL,
            Trimestre TINYINT NOT NULL
        );
    END;

    IF OBJECT_ID(N'dw.DimProducto', N'U') IS NULL
    BEGIN
        CREATE TABLE dw.DimProducto (
            ProductoKey INT IDENTITY(1,1) CONSTRAINT PK_DimProducto PRIMARY KEY,
            ProductoIdOriginal NVARCHAR(100) NOT NULL,
            SistemaOrigen NVARCHAR(50) NOT NULL,
            Nombre NVARCHAR(200) NULL,
            Categoria NVARCHAR(150) NULL,
            CONSTRAINT UQ_DimProducto_Origen UNIQUE (SistemaOrigen, ProductoIdOriginal)
        );
    END;

    IF OBJECT_ID(N'dw.DimCliente', N'U') IS NULL
    BEGIN
        CREATE TABLE dw.DimCliente (
            ClienteKey INT IDENTITY(1,1) CONSTRAINT PK_DimCliente PRIMARY KEY,
            ClienteIdOriginal NVARCHAR(100) NOT NULL,
            SistemaOrigen NVARCHAR(50) NOT NULL,
            Nombre NVARCHAR(200) NULL,
            Email NVARCHAR(200) NULL,
            CONSTRAINT UQ_DimCliente_Origen UNIQUE (SistemaOrigen, ClienteIdOriginal)
        );
    END;

    IF OBJECT_ID(N'dw.DimFuente', N'U') IS NULL
    BEGIN
        CREATE TABLE dw.DimFuente (
            FuenteKey INT IDENTITY(1,1) CONSTRAINT PK_DimFuente PRIMARY KEY,
            CodigoFuente NVARCHAR(50) NOT NULL CONSTRAINT UQ_DimFuente_Codigo UNIQUE,
            TipoFuente NVARCHAR(30) NOT NULL,
            NombreFuente NVARCHAR(150) NOT NULL
        );
    END;

    IF OBJECT_ID(N'dw.DimClasificacion', N'U') IS NULL
    BEGIN
        CREATE TABLE dw.DimClasificacion (
            ClasificacionKey INT IDENTITY(1,1) CONSTRAINT PK_DimClasificacion PRIMARY KEY,
            Nombre NVARCHAR(30) NOT NULL CONSTRAINT UQ_DimClasificacion_Nombre UNIQUE
        );
    END;

    IF OBJECT_ID(N'dw.FactOpinion', N'U') IS NULL
    BEGIN
        CREATE TABLE dw.FactOpinion (
            OpinionKey BIGINT IDENTITY(1,1) CONSTRAINT PK_FactOpinion PRIMARY KEY,
            OpinionIdOriginal NVARCHAR(100) NOT NULL,
            FechaKey INT NULL,
            ProductoKey INT NULL,
            ClienteKey INT NULL,
            FuenteKey INT NOT NULL,
            ClasificacionKey INT NULL,
            Comentario NVARCHAR(MAX) NULL,
            PuntajeSatisfaccion DECIMAL(5,2) NULL,
            CantidadOpiniones SMALLINT NOT NULL CONSTRAINT DF_Fact_Cantidad DEFAULT (1),
            FechaCargaUtc DATETIME2 NOT NULL CONSTRAINT DF_Fact_FechaCarga DEFAULT SYSUTCDATETIME(),
            CONSTRAINT CK_Fact_Cantidad CHECK (CantidadOpiniones = 1),
            CONSTRAINT UQ_Fact_OpinionOrigen UNIQUE (FuenteKey, OpinionIdOriginal),
            CONSTRAINT FK_Fact_Fecha FOREIGN KEY (FechaKey) REFERENCES dw.DimFecha(FechaKey),
            CONSTRAINT FK_Fact_Producto FOREIGN KEY (ProductoKey) REFERENCES dw.DimProducto(ProductoKey),
            CONSTRAINT FK_Fact_Cliente FOREIGN KEY (ClienteKey) REFERENCES dw.DimCliente(ClienteKey),
            CONSTRAINT FK_Fact_Fuente FOREIGN KEY (FuenteKey) REFERENCES dw.DimFuente(FuenteKey),
            CONSTRAINT FK_Fact_Clasificacion FOREIGN KEY (ClasificacionKey)
                REFERENCES dw.DimClasificacion(ClasificacionKey)
        );
        CREATE INDEX IX_Fact_Clasificacion ON dw.FactOpinion(ClasificacionKey);
        CREATE INDEX IX_Fact_Fecha ON dw.FactOpinion(FechaKey);
        CREATE INDEX IX_Fact_Producto ON dw.FactOpinion(ProductoKey);
    END;

    INSERT INTO config.FuenteDatos (Codigo, Nombre, Tipo)
    SELECT fuente.Codigo, fuente.Nombre, fuente.Tipo
    FROM (VALUES
        (N'ENCUESTAS', N'Encuestas internas', N'CSV'),
        (N'RESENAS_WEB', N'Resenas web', N'SQL'),
        (N'REDES_SOCIALES', N'Comentarios sociales', N'API')
    ) AS fuente(Codigo, Nombre, Tipo)
    WHERE NOT EXISTS (
        SELECT 1 FROM config.FuenteDatos AS existente WITH (UPDLOCK, HOLDLOCK)
        WHERE existente.Codigo = fuente.Codigo
    );

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
