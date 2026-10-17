-- Target Database Schema (Current Production / v1)
CREATE TABLE [dbo].[Customers] (
    [Id] INT IDENTITY(1,1) NOT NULL,
    [CustomerCode] VARCHAR(10) NOT NULL,
    [FullName] NVARCHAR(100) NOT NULL,
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

CREATE PROCEDURE [dbo].[usp_GetCustomerOrders]
    @CustomerId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT OrderId, TotalAmount FROM dbo.Orders WHERE CustomerId = @CustomerId;
END;
GO
