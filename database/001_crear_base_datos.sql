USE master;
GO

IF DB_ID(N'AnalisisOpinionesClientes') IS NULL
    CREATE DATABASE AnalisisOpinionesClientes;
GO

USE AnalisisOpinionesClientes;
GO

IF DB_NAME() <> N'AnalisisOpinionesClientes'
    THROW 50000, 'Ejecute el script en AnalisisOpinionesClientes.', 1;

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET QUOTED_IDENTIFIER ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF SCHEMA_ID(N'origen') IS NULL
        EXEC(N'CREATE SCHEMA origen AUTHORIZATION dbo;');

    IF SCHEMA_ID(N'staging') IS NULL
        EXEC(N'CREATE SCHEMA staging AUTHORIZATION dbo;');

    IF OBJECT_ID(N'dbo.cliente', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.cliente (
            id_cliente INT IDENTITY(1,1) CONSTRAINT PK_cliente PRIMARY KEY,
            nombre VARCHAR(150) NOT NULL,
            email VARCHAR(150) NULL,
            CONSTRAINT CK_cliente_id CHECK (id_cliente > 0),
            CONSTRAINT CK_cliente_nombre CHECK (LEN(LTRIM(RTRIM(nombre))) > 0)
        );

        CREATE UNIQUE INDEX UX_cliente_email ON dbo.cliente(email)
        WHERE email IS NOT NULL;
    END;

    IF OBJECT_ID(N'dbo.categoria', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.categoria (
            id_categoria INT IDENTITY(1,1) CONSTRAINT PK_categoria PRIMARY KEY,
            nombre VARCHAR(100) NOT NULL CONSTRAINT UQ_categoria_nombre UNIQUE,
            CONSTRAINT CK_categoria_nombre CHECK (LEN(LTRIM(RTRIM(nombre))) > 0)
        );
    END;

    IF OBJECT_ID(N'dbo.producto', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.producto (
            id_producto INT IDENTITY(1,1) CONSTRAINT PK_producto PRIMARY KEY,
            id_categoria INT NOT NULL,
            nombre VARCHAR(150) NOT NULL CONSTRAINT UQ_producto_nombre UNIQUE,
            CONSTRAINT FK_producto_categoria FOREIGN KEY (id_categoria)
                REFERENCES dbo.categoria(id_categoria),
            CONSTRAINT CK_producto_id CHECK (id_producto > 0),
            CONSTRAINT CK_producto_nombre CHECK (LEN(LTRIM(RTRIM(nombre))) > 0)
        );
    END;

    IF OBJECT_ID(N'dbo.fuente_datos', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.fuente_datos (
            id_fuente INT IDENTITY(1,1) CONSTRAINT PK_fuente_datos PRIMARY KEY,
            tipo_fuente VARCHAR(50) NOT NULL,
            nombre_fuente VARCHAR(150) NULL,
            fecha_carga DATETIME2 NOT NULL CONSTRAINT DF_fuente_fecha_carga DEFAULT SYSDATETIME(),
            codigo_fuente_externo VARCHAR(10) NULL,
            CONSTRAINT CK_fuente_identificada CHECK (
                codigo_fuente_externo IS NOT NULL OR nombre_fuente IS NOT NULL
            ),
            CONSTRAINT CK_fuente_tipo CHECK (LEN(LTRIM(RTRIM(tipo_fuente))) > 0),
            CONSTRAINT CK_fuente_codigo CHECK (
                codigo_fuente_externo IS NULL OR LEN(LTRIM(RTRIM(codigo_fuente_externo))) > 0
            ),
            CONSTRAINT CK_fuente_nombre CHECK (
                nombre_fuente IS NULL OR LEN(LTRIM(RTRIM(nombre_fuente))) > 0
            )
        );

        CREATE UNIQUE INDEX UX_fuente_codigo_externo ON dbo.fuente_datos(codigo_fuente_externo)
        WHERE codigo_fuente_externo IS NOT NULL;

        CREATE UNIQUE INDEX UX_fuente_nombre_sin_codigo ON dbo.fuente_datos(tipo_fuente, nombre_fuente)
        WHERE codigo_fuente_externo IS NULL AND nombre_fuente IS NOT NULL;
    END;

    IF OBJECT_ID(N'dbo.clasificacion', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.clasificacion (
            id_clasificacion INT IDENTITY(1,1) CONSTRAINT PK_clasificacion PRIMARY KEY,
            nombre VARCHAR(20) NOT NULL CONSTRAINT UQ_clasificacion_nombre UNIQUE,
            CONSTRAINT CK_clasificacion_nombre CHECK (nombre IN ('Positiva', 'Negativa', 'Neutra'))
        );
    END;

    IF OBJECT_ID(N'dbo.opinion', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.opinion (
            id_opinion INT IDENTITY(1,1) CONSTRAINT PK_opinion PRIMARY KEY,
            id_cliente INT NULL,
            id_producto INT NOT NULL,
            id_fuente INT NOT NULL,
            id_clasificacion INT NOT NULL,
            fecha DATETIME2 NOT NULL,
            comentario NVARCHAR(MAX) NOT NULL,
            puntaje_satisfaccion DECIMAL(5,2) NULL,
            codigo_opinion_externo VARCHAR(30) NOT NULL,
            origen_archivo VARCHAR(80) NOT NULL,
            CONSTRAINT FK_opinion_cliente FOREIGN KEY (id_cliente) REFERENCES dbo.cliente(id_cliente),
            CONSTRAINT FK_opinion_producto FOREIGN KEY (id_producto) REFERENCES dbo.producto(id_producto),
            CONSTRAINT FK_opinion_fuente FOREIGN KEY (id_fuente) REFERENCES dbo.fuente_datos(id_fuente),
            CONSTRAINT FK_opinion_clasificacion FOREIGN KEY (id_clasificacion)
                REFERENCES dbo.clasificacion(id_clasificacion),
            CONSTRAINT CK_opinion_puntaje CHECK (puntaje_satisfaccion BETWEEN 1 AND 5),
            CONSTRAINT CK_opinion_codigo CHECK (LEN(LTRIM(RTRIM(codigo_opinion_externo))) > 0),
            CONSTRAINT CK_opinion_archivo CHECK (LEN(LTRIM(RTRIM(origen_archivo))) > 0),
            CONSTRAINT CK_opinion_comentario CHECK (LEN(LTRIM(RTRIM(comentario))) > 0)
        );

        CREATE UNIQUE INDEX UX_opinion_archivo_codigo ON dbo.opinion(origen_archivo, codigo_opinion_externo);
    END;

    IF OBJECT_ID(N'origen.resena_web', N'U') IS NULL
    BEGIN
        -- Los seis campos del CSV se conservan como texto Unicode.
        CREATE TABLE origen.resena_web (
            id_resena_web BIGINT IDENTITY(1,1) CONSTRAINT PK_resena_web PRIMARY KEY,
            IdReview NVARCHAR(MAX) NULL,
            IdCliente NVARCHAR(MAX) NULL,
            IdProducto NVARCHAR(MAX) NULL,
            Fecha NVARCHAR(MAX) NULL,
            Comentario NVARCHAR(MAX) NULL,
            Rating NVARCHAR(MAX) NULL
        );
    END;

    IF OBJECT_ID(N'staging.opinion_extraida', N'U') IS NULL
    BEGIN
        CREATE TABLE staging.opinion_extraida (
            id_opinion_extraida BIGINT IDENTITY(1,1) CONSTRAINT PK_opinion_extraida PRIMARY KEY,
            id_ejecucion UNIQUEIDENTIFIER NOT NULL,
            fuente NVARCHAR(150) NOT NULL,
            id_externo NVARCHAR(128) NULL,
            -- El JSON conserva los campos de la fuente con sus valores originales.
            datos_originales NVARCHAR(MAX) NOT NULL,
            fecha_extraccion_utc DATETIME2 NOT NULL
                CONSTRAINT DF_opinion_extraida_fecha DEFAULT SYSUTCDATETIME(),
            CONSTRAINT CK_opinion_extraida_fuente CHECK (LEN(LTRIM(RTRIM(fuente))) > 0),
            CONSTRAINT CK_opinion_extraida_json CHECK (ISJSON(datos_originales) = 1)
        );

        CREATE INDEX IX_opinion_extraida_ejecucion ON staging.opinion_extraida(id_ejecucion);
    END;

    INSERT INTO dbo.clasificacion (nombre)
    SELECT catalogo.nombre
    FROM (VALUES ('Positiva'), ('Negativa'), ('Neutra')) AS catalogo(nombre)
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.clasificacion AS existente WHERE existente.nombre = catalogo.nombre
    );

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;

    THROW;
END CATCH;
GO
