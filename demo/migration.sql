/*
===============================================================================
 SQLDrift Migration Script
 Generated: 2026-10-16 09:31:20 UTC
 Source:    demo/source_v2.sql
 Target:    demo/target_v1.sql
 Steps:     8
 Breaking:  False (AllowDataLoss=False)
===============================================================================
*/

SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRANSACTION [SqlDrift_Sync];

    -- Step 2.TABLE_CREATE_dbo_Invoices: CREATE_TABLE on Table [dbo].[Invoices] (Severity: Low)
    IF NOT EXISTS (
        SELECT 1 FROM sys.tables t
        JOIN sys.schemas s ON t.schema_id = s.schema_id
        WHERE s.name = N'dbo' AND t.name = N'Invoices'
    )
    BEGIN
        CREATE TABLE [dbo].[Invoices] (
        [InvoiceId] INT IDENTITY(1,1) NOT NULL,
        [OrderId] INT NOT NULL,
        [InvoiceNumber] VARCHAR(50) NOT NULL,
        [DueDate] DATETIME2(7) NOT NULL,
        CONSTRAINT [PK_Invoices] PRIMARY KEY CLUSTERED ([InvoiceId])
        );
        PRINT 'Created table [dbo].[Invoices]';
    END

    -- Step 3.COL_ADD_dbo_Customers_Email: ADD_COLUMN on Column [dbo].[Email] (Severity: Low)
    IF NOT EXISTS (
        SELECT 1 FROM sys.columns c
        JOIN sys.tables t ON c.object_id = t.object_id
        JOIN sys.schemas s ON t.schema_id = s.schema_id
        WHERE s.name = N'dbo' AND t.name = N'Customers' AND c.name = N'Email'
    )
    BEGIN
        ALTER TABLE [dbo].[Customers] ADD [Email] NVARCHAR(255) NULL;
        PRINT 'Added column [dbo].[Customers].[Email]';
    END

    -- Step 3.COL_ADD_dbo_Customers_PhoneNumber: ADD_COLUMN on Column [dbo].[PhoneNumber] (Severity: Low)
    IF NOT EXISTS (
        SELECT 1 FROM sys.columns c
        JOIN sys.tables t ON c.object_id = t.object_id
        JOIN sys.schemas s ON t.schema_id = s.schema_id
        WHERE s.name = N'dbo' AND t.name = N'Customers' AND c.name = N'PhoneNumber'
    )
    BEGIN
        ALTER TABLE [dbo].[Customers] ADD [PhoneNumber] NVARCHAR(50) NULL;
        PRINT 'Added column [dbo].[Customers].[PhoneNumber]';
    END

    -- Step 3.COL_ALTER_dbo_Customers_CustomerCode: ALTER_COLUMN on Column [dbo].[CustomerCode] (Severity: Medium)
    ALTER TABLE [dbo].[Customers] ALTER COLUMN [CustomerCode] VARCHAR(25) NOT NULL;
    PRINT 'Altered column [dbo].[Customers].[CustomerCode] to VARCHAR(25) NOT NULL';

    -- Step 3.IDX_ADD_dbo_Orders_IX_Orders_OrderDate: ADD_INDEX on Index [dbo].[IX_Orders_OrderDate] (Severity: Low)
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes i
        JOIN sys.tables t ON i.object_id = t.object_id
        JOIN sys.schemas s ON t.schema_id = s.schema_id
        WHERE s.name = N'dbo' AND t.name = N'Orders' AND i.name = N'IX_Orders_OrderDate'
    )
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_Orders_OrderDate] ON [dbo].[Orders] ([OrderDate]) INCLUDE ([TotalAmount]);
        PRINT 'Created index [IX_Orders_OrderDate] on [dbo].[Orders]';
    END

    -- Step 4.FK_ADD_dbo_Invoices_FK_Invoices_Orders: ADD_FOREIGN_KEY on ForeignKey [dbo].[FK_Invoices_Orders] (Severity: Low)
    IF NOT EXISTS (
        SELECT 1 FROM sys.foreign_keys fk
        JOIN sys.tables t ON fk.parent_object_id = t.object_id
        JOIN sys.schemas s ON t.schema_id = s.schema_id
        WHERE s.name = N'dbo' AND t.name = N'Invoices' AND fk.name = N'FK_Invoices_Orders'
    )
    BEGIN
        ALTER TABLE [dbo].[Invoices]
        ADD CONSTRAINT [FK_Invoices_Orders] FOREIGN KEY ([OrderId])
        REFERENCES [dbo].[Orders] ([OrderId]);
        PRINT 'Added foreign key [FK_Invoices_Orders]';
    END

    -- Step 7.VIEW_dbo_vw_HighValueCustomers: CREATE_OR_ALTER_VIEW on View [dbo].[vw_HighValueCustomers] (Severity: Low)
    EXEC(N'CREATE OR ALTER VIEW [dbo].[vw_HighValueCustomers]
AS
SELECT   c.Id,
         c.FullName,
         SUM(o.TotalAmount) AS TotalSpent
FROM     dbo.Customers AS c
         INNER JOIN
         dbo.Orders AS o
         ON c.Id = o.CustomerId
GROUP BY c.Id, c.FullName
HAVING   SUM(o.TotalAmount) > 5000');
    PRINT 'Updated View [dbo].[vw_HighValueCustomers]';

    -- Step 8.PROCEDURE_dbo_usp_GetCustomerOrders: CREATE_OR_ALTER_PROCEDURE on StoredProcedure [dbo].[usp_GetCustomerOrders] (Severity: Medium)
    EXEC(N'CREATE OR ALTER PROCEDURE [dbo].[usp_GetCustomerOrders]
@CustomerId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT OrderId,
           OrderDate,
           TotalAmount
    FROM   dbo.Orders
    WHERE  CustomerId = @CustomerId;
END');
    PRINT 'Updated StoredProcedure [dbo].[usp_GetCustomerOrders]';

    COMMIT TRANSACTION [SqlDrift_Sync];
    PRINT '==================================================';
    PRINT 'SQLDrift: Migration completed successfully.';
    PRINT '==================================================';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION [SqlDrift_Sync];
    DECLARE @ErrMsg NVARCHAR(4000) = ERROR_MESSAGE();
    DECLARE @ErrSev INT = ERROR_SEVERITY();
    DECLARE @ErrState INT = ERROR_STATE();
    RAISERROR(@ErrMsg, @ErrSev, @ErrState);
END CATCH;
