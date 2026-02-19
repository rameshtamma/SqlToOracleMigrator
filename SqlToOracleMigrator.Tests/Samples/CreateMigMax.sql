/*
  CreateMigMax.sql
  Creates a medium-size SQL Server test database "MigMax" for SqlToOracleMigrator validation.

  Highlights:
  - Multiple schemas (dbo, sales, hr)
  - Tables with PK/FK, unique constraints, check constraints, defaults
  - Indexes (clustered/nonclustered, filtered)
  - Views, procedures, functions, triggers
  - Sequences
  - Seed data (10s of thousands of rows)

  Usage:
    - Run in SSMS as sysadmin (or with create db permissions)
    - You can change DB name by editing the first DECLARE.
*/

SET NOCOUNT ON;
DECLARE @DbName sysname = N'MigMax';

IF DB_ID(@DbName) IS NOT NULL
BEGIN
    PRINT 'Dropping existing database ' + @DbName;
    EXEC('ALTER DATABASE [' + @DbName + '] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;');
    EXEC('DROP DATABASE [' + @DbName + '];');
END
GO

DECLARE @DbName2 sysname = N'MigMax';
EXEC('CREATE DATABASE [' + @DbName2 + '];');
GO

USE [MigMax];
GO

-- Schemas
CREATE SCHEMA sales AUTHORIZATION dbo;
GO
CREATE SCHEMA hr AUTHORIZATION dbo;
GO

-- Sequences
CREATE SEQUENCE dbo.Seq_OrderNumber START WITH 100000 INCREMENT BY 1;
GO
CREATE SEQUENCE sales.Seq_InvoiceNumber START WITH 500000 INCREMENT BY 1;
GO

-- Core reference tables
CREATE TABLE dbo.Country(
    CountryCode char(2) NOT NULL CONSTRAINT PK_Country PRIMARY KEY,
    CountryName nvarchar(100) NOT NULL
);
GO

CREATE TABLE dbo.State(
    StateCode char(2) NOT NULL,
    CountryCode char(2) NOT NULL,
    StateName nvarchar(100) NOT NULL,
    CONSTRAINT PK_State PRIMARY KEY (CountryCode, StateCode),
    CONSTRAINT FK_State_Country FOREIGN KEY (CountryCode) REFERENCES dbo.Country(CountryCode)
);
GO

CREATE TABLE dbo.Customer(
    CustomerId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Customer PRIMARY KEY,
    ExternalCustomerKey uniqueidentifier NOT NULL CONSTRAINT UQ_Customer_External UNIQUE,
    FullName nvarchar(200) NOT NULL,
    Email nvarchar(320) NULL,
    Phone nvarchar(30) NULL,
    CountryCode char(2) NOT NULL,
    StateCode char(2) NULL,
    CreatedUtc datetime2(7) NOT NULL CONSTRAINT DF_Customer_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    Status tinyint NOT NULL CONSTRAINT DF_Customer_Status DEFAULT (1),
    CONSTRAINT FK_Customer_Country FOREIGN KEY (CountryCode) REFERENCES dbo.Country(CountryCode),
    CONSTRAINT FK_Customer_State FOREIGN KEY (CountryCode, StateCode) REFERENCES dbo.State(CountryCode, StateCode),
    CONSTRAINT CK_Customer_Status CHECK (Status in (0,1,2))
);
GO

CREATE INDEX IX_Customer_Email ON dbo.Customer(Email) WHERE Email IS NOT NULL;
GO

CREATE TABLE dbo.Product(
    ProductId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Product PRIMARY KEY,
    Sku nvarchar(64) NOT NULL CONSTRAINT UQ_Product_Sku UNIQUE,
    ProductName nvarchar(200) NOT NULL,
    Category nvarchar(100) NOT NULL,
    UnitPrice decimal(18,2) NOT NULL,
    IsActive bit NOT NULL CONSTRAINT DF_Product_IsActive DEFAULT (1),
    CreatedUtc datetime2(7) NOT NULL CONSTRAINT DF_Product_CreatedUtc DEFAULT (SYSUTCDATETIME())
);
GO

CREATE TABLE dbo.Warehouse(
    WarehouseId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Warehouse PRIMARY KEY,
    WarehouseName nvarchar(150) NOT NULL,
    CountryCode char(2) NOT NULL,
    StateCode char(2) NULL,
    CONSTRAINT FK_Warehouse_Country FOREIGN KEY (CountryCode) REFERENCES dbo.Country(CountryCode),
    CONSTRAINT FK_Warehouse_State FOREIGN KEY (CountryCode, StateCode) REFERENCES dbo.State(CountryCode, StateCode)
);
GO

CREATE TABLE dbo.Inventory(
    WarehouseId int NOT NULL,
    ProductId int NOT NULL,
    OnHandQty int NOT NULL CONSTRAINT DF_Inventory_Qty DEFAULT(0),
    UpdatedUtc datetime2(7) NOT NULL CONSTRAINT DF_Inventory_UpdatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_Inventory PRIMARY KEY (WarehouseId, ProductId),
    CONSTRAINT FK_Inventory_Warehouse FOREIGN KEY (WarehouseId) REFERENCES dbo.Warehouse(WarehouseId),
    CONSTRAINT FK_Inventory_Product FOREIGN KEY (ProductId) REFERENCES dbo.Product(ProductId),
    CONSTRAINT CK_Inventory_Qty CHECK (OnHandQty >= 0)
);
GO

-- Orders
CREATE TABLE sales.[Order](
    OrderId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_Order PRIMARY KEY,
    OrderNumber bigint NOT NULL CONSTRAINT UQ_Order_OrderNumber UNIQUE,
    CustomerId int NOT NULL,
    OrderStatus tinyint NOT NULL CONSTRAINT DF_Order_Status DEFAULT (1),
    OrderUtc datetime2(7) NOT NULL CONSTRAINT DF_Order_OrderUtc DEFAULT (SYSUTCDATETIME()),
    Notes nvarchar(1000) NULL,
    CONSTRAINT FK_Order_Customer FOREIGN KEY (CustomerId) REFERENCES dbo.Customer(CustomerId),
    CONSTRAINT CK_Order_Status CHECK (OrderStatus in (0,1,2,3))
);
GO

CREATE INDEX IX_Order_CustomerUtc ON sales.[Order](CustomerId, OrderUtc);
GO

CREATE TABLE sales.OrderLine(
    OrderLineId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrderLine PRIMARY KEY,
    OrderId bigint NOT NULL,
    ProductId int NOT NULL,
    Quantity int NOT NULL,
    UnitPrice decimal(18,2) NOT NULL,
    LineTotal AS (CONVERT(decimal(18,2), Quantity * UnitPrice)) PERSISTED,
    CONSTRAINT FK_OrderLine_Order FOREIGN KEY (OrderId) REFERENCES sales.[Order](OrderId),
    CONSTRAINT FK_OrderLine_Product FOREIGN KEY (ProductId) REFERENCES dbo.Product(ProductId),
    CONSTRAINT CK_OrderLine_Qty CHECK (Quantity > 0)
);
GO

CREATE INDEX IX_OrderLine_Order ON sales.OrderLine(OrderId);
GO

-- HR
CREATE TABLE hr.Employee(
    EmployeeId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Employee PRIMARY KEY,
    EmployeeNumber nvarchar(20) NOT NULL CONSTRAINT UQ_Employee_Number UNIQUE,
    FullName nvarchar(200) NOT NULL,
    HireDate date NOT NULL,
    TerminationDate date NULL,
    Salary decimal(18,2) NOT NULL,
    ManagerEmployeeId int NULL,
    CONSTRAINT FK_Employee_Manager FOREIGN KEY (ManagerEmployeeId) REFERENCES hr.Employee(EmployeeId)
);
GO

CREATE TABLE hr.Timesheet(
    TimesheetId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_Timesheet PRIMARY KEY,
    EmployeeId int NOT NULL,
    WorkDate date NOT NULL,
    StartTime time(0) NOT NULL,
    EndTime time(0) NOT NULL,
    Notes nvarchar(200) NULL,
    CONSTRAINT FK_Timesheet_Employee FOREIGN KEY (EmployeeId) REFERENCES hr.Employee(EmployeeId),
    CONSTRAINT CK_Timesheet_Time CHECK (EndTime > StartTime)
);
GO

-- Views
CREATE VIEW sales.v_OrderSummary AS
SELECT o.OrderId, o.OrderNumber, o.CustomerId, c.FullName, o.OrderUtc,
       SUM(ol.LineTotal) AS OrderTotal,
       COUNT(*) AS LineCount
FROM sales.[Order] o
JOIN dbo.Customer c ON c.CustomerId = o.CustomerId
JOIN sales.OrderLine ol ON ol.OrderId = o.OrderId
GROUP BY o.OrderId, o.OrderNumber, o.CustomerId, c.FullName, o.OrderUtc;
GO

-- Functions
CREATE FUNCTION dbo.fn_NormalizeEmail(@Email nvarchar(320))
RETURNS nvarchar(320)
AS
BEGIN
    RETURN LOWER(LTRIM(RTRIM(@Email)));
END
GO

-- Procedures
CREATE PROCEDURE sales.usp_CreateOrder
    @CustomerId int,
    @Notes nvarchar(1000) = NULL,
    @OrderId bigint OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @OrderNumber bigint = NEXT VALUE FOR dbo.Seq_OrderNumber;

    INSERT INTO sales.[Order](OrderNumber, CustomerId, Notes)
    VALUES(@OrderNumber, @CustomerId, @Notes);

    SET @OrderId = SCOPE_IDENTITY();
END
GO

-- Triggers
CREATE TRIGGER dbo.tr_Customer_EmailNormalize
ON dbo.Customer
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE c
    SET Email = dbo.fn_NormalizeEmail(i.Email)
    FROM dbo.Customer c
    JOIN inserted i ON i.CustomerId = c.CustomerId
    WHERE i.Email IS NOT NULL;
END
GO

-- Seed data
INSERT INTO dbo.Country(CountryCode, CountryName)
VALUES ('US','United States'), ('CA','Canada');

INSERT INTO dbo.State(StateCode, CountryCode, StateName)
VALUES ('NJ','US','New Jersey'),('NY','US','New York'),('CA','US','California'),('ON','CA','Ontario');

-- Employees
;WITH n AS (
    SELECT TOP (200) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS rn
    FROM sys.all_objects
)
INSERT INTO hr.Employee(EmployeeNumber, FullName, HireDate, Salary, ManagerEmployeeId)
SELECT CONCAT('E', FORMAT(rn,'00000')),
       CONCAT('Employee ', rn),
       DATEADD(DAY, -rn*7, CAST(GETDATE() AS date)),
       60000 + (rn % 50) * 1000,
       CASE WHEN rn <= 10 THEN NULL ELSE (rn % 10) + 1 END
FROM n;

-- Products
;WITH n AS (
    SELECT TOP (500) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS rn
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT INTO dbo.Product(Sku, ProductName, Category, UnitPrice)
SELECT CONCAT('SKU-', FORMAT(rn,'000000')),
       CONCAT('Product ', rn),
       CASE WHEN rn % 5 = 0 THEN 'Hardware'
            WHEN rn % 5 = 1 THEN 'Software'
            WHEN rn % 5 = 2 THEN 'Office'
            WHEN rn % 5 = 3 THEN 'Books'
            ELSE 'Apparel' END,
       CAST(5.00 + (rn % 250) * 0.75 AS decimal(18,2))
FROM n;

-- Warehouses
INSERT INTO dbo.Warehouse(WarehouseName, CountryCode, StateCode)
VALUES ('East DC','US','NJ'),('West DC','US','CA'),('Canada DC','CA','ON');

-- Customers
;WITH n AS (
    SELECT TOP (20000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS rn
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT INTO dbo.Customer(ExternalCustomerKey, FullName, Email, Phone, CountryCode, StateCode, Status)
SELECT NEWID(),
       CONCAT('Customer ', rn),
       CONCAT('customer', rn, '@example.com'),
       CONCAT('+1-555-', FORMAT(rn % 10000, '0000')),
       'US',
       CASE WHEN rn % 3 = 0 THEN 'NJ' WHEN rn % 3 = 1 THEN 'NY' ELSE 'CA' END,
       CASE WHEN rn % 20 = 0 THEN 2 ELSE 1 END
FROM n;

-- Inventory
;WITH p AS (SELECT ProductId FROM dbo.Product),
     w AS (SELECT WarehouseId FROM dbo.Warehouse)
INSERT INTO dbo.Inventory(WarehouseId, ProductId, OnHandQty)
SELECT w.WarehouseId, p.ProductId, ABS(CHECKSUM(NEWID())) % 1000
FROM w CROSS JOIN p;

-- Orders + lines
DECLARE @i int = 1;
WHILE @i <= 50000
BEGIN
    DECLARE @cust int = 1 + (ABS(CHECKSUM(NEWID())) % 20000);
    DECLARE @orderId bigint;
    DECLARE @notes nvarchar(200) = CASE WHEN @i % 10 = 0 THEN N'priority' ELSE NULL END;
    EXEC sales.usp_CreateOrder @CustomerId=@cust, @Notes=@notes, @OrderId=@orderId OUTPUT;

    DECLARE @lines int = 1 + (ABS(CHECKSUM(NEWID())) % 6);
    DECLARE @j int = 1;
    WHILE @j <= @lines
    BEGIN
        DECLARE @prod int = 1 + (ABS(CHECKSUM(NEWID())) % 500);
        DECLARE @qty int = 1 + (ABS(CHECKSUM(NEWID())) % 5);
        DECLARE @price decimal(18,2) = (SELECT UnitPrice FROM dbo.Product WHERE ProductId = @prod);
        INSERT INTO sales.OrderLine(OrderId, ProductId, Quantity, UnitPrice)
        VALUES(@orderId, @prod, @qty, @price);
        SET @j += 1;
    END

    SET @i += 1;
END

-- Timesheets (time datatype intentionally included)
INSERT INTO hr.Timesheet(EmployeeId, WorkDate, StartTime, EndTime, Notes)
SELECT TOP (50000)
       1 + (rn % 200),
       DATEADD(DAY, -(rn % 365), CAST(GETDATE() AS date)),
       CAST('09:00:00' AS time(0)),
       CAST('17:00:00' AS time(0)),
       CASE WHEN rn % 100 = 0 THEN N'overtime' ELSE NULL END
FROM (
    SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS rn
    FROM sys.all_objects a
    CROSS JOIN sys.all_objects b
) n;


PRINT 'MigMax created successfully.';
