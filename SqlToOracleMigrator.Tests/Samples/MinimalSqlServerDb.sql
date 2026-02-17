/*
Minimal baseline DB for SqlToOracleMigrator.
Goal: small schema with FK + index + identity + datetime + nvarchar(max).
Run on SQL Server (any edition).

Creates database: MigMini
Creates schemas: dbo
Creates tables: Customers, Orders
*/

IF DB_ID('MigMini') IS NOT NULL
BEGIN
  ALTER DATABASE MigMini SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
  DROP DATABASE MigMini;
END;
GO

CREATE DATABASE MigMini;
GO
USE MigMini;
GO

CREATE TABLE dbo.Customers (
    CustomerId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Email NVARCHAR(256) NOT NULL,
    FullName NVARCHAR(200) NOT NULL,
    CreatedUtc DATETIME2(3) NOT NULL CONSTRAINT DF_Customers_CreatedUtc DEFAULT SYSUTCDATETIME()
);
GO

CREATE UNIQUE INDEX IX_Customers_Email ON dbo.Customers(Email);
GO

CREATE TABLE dbo.Orders (
    OrderId BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CustomerId INT NOT NULL,
    OrderTotal DECIMAL(18,2) NOT NULL,
    Notes NVARCHAR(MAX) NULL,
    PlacedUtc DATETIME2(3) NOT NULL,
    CONSTRAINT FK_Orders_Customers FOREIGN KEY(CustomerId) REFERENCES dbo.Customers(CustomerId)
);
GO

INSERT dbo.Customers(Email, FullName) VALUES
 ('a@example.com','Alice Example'),
 ('b@example.com','Bob Example');
GO

INSERT dbo.Orders(CustomerId, OrderTotal, Notes, PlacedUtc) VALUES
 (1, 120.55, N'First order', SYSUTCDATETIME()),
 (1,  20.00, NULL, SYSUTCDATETIME()),
 (2,  99.99, N'Gift order', SYSUTCDATETIME());
GO
