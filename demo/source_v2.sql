-- Source Database Schema (Desired Staging/Dev / v2)
CREATE TABLE [dbo].[Customers] (
    [Id] INT IDENTITY(1,1) NOT NULL,
    [CustomerCode] VARCHAR(25) NOT NULL, -- Widened from VARCHAR(10)
    [FullName] NVARCHAR(100) NOT NULL,
    [Email] NVARCHAR(255) NULL,          -- New column (safe addition)
    [PhoneNumber] NVARCHAR(50) NULL,     -- New column (safe addition)
    [Balance] DECIMAL(18,2) NOT NULL DEFAULT (0.00),
    CONSTRAINT [PK_Customers] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [CK_Customers_Balance] CHECK ([Balance] >= 0)
);
GO

CREATE TABLE [dbo].[Orders] (
    [OrderId] INT IDENTITY(1,1) NOT NULL,
    [CustomerId] INT NOT NULL,
    [OrderDate] DATETIME2 NOT NULL,
    [TotalAmount] DECIMAL(18,2) NOT NULL,
    CONSTRAINT [PK_Orders] PRIMARY KEY CLUSTERED ([OrderId]),
    CONSTRAINT [FK_Orders_Customers] FOREIGN KEY ([CustomerId]) REFERENCES [dbo].[Customers] ([Id])
);
GO

CREATE NONCLUSTERED INDEX [IX_Orders_OrderDate]
ON [dbo].[Orders] ([OrderDate])
INCLUDE ([TotalAmount]);
GO

-- New Table
CREATE TABLE [dbo].[Invoices] (
    [InvoiceId] INT IDENTITY(1,1) NOT NULL,
    [OrderId] INT NOT NULL,
    [InvoiceNumber] VARCHAR(50) NOT NULL,
    [DueDate] DATETIME2 NOT NULL,
    CONSTRAINT [PK_Invoices] PRIMARY KEY CLUSTERED ([InvoiceId]),
    CONSTRAINT [FK_Invoices_Orders] FOREIGN KEY ([OrderId]) REFERENCES [dbo].[Orders] ([OrderId])
);
GO

-- Modified Stored Procedure
CREATE PROCEDURE [dbo].[usp_GetCustomerOrders]
    @CustomerId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT OrderId, OrderDate, TotalAmount FROM dbo.Orders WHERE CustomerId = @CustomerId;
END;
GO

-- New View
CREATE VIEW [dbo].[vw_HighValueCustomers]
AS
    SELECT c.Id, c.FullName, SUM(o.TotalAmount) AS TotalSpent
    FROM dbo.Customers c
    JOIN dbo.Orders o ON c.Id = o.CustomerId
    GROUP BY c.Id, c.FullName
    HAVING SUM(o.TotalAmount) > 5000;
GO
