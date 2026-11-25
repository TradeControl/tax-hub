SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF SCHEMA_ID(N'Tax') IS NULL EXEC(N'CREATE SCHEMA Tax AUTHORIZATION dbo;');
GO

IF OBJECT_ID(N'Tax.CorporationTaxReview', N'U') IS NULL
BEGIN
    CREATE TABLE Tax.CorporationTaxReview
    (
        TenantReference uniqueidentifier NOT NULL,
        LogicalFilingIdentity char(64) NOT NULL,
        AccountsSnapshotSha256 char(64) NOT NULL,
        PeriodStart date NOT NULL,
        PeriodEnd date NOT NULL,
        SourceSha256 char(64) NOT NULL,
        InputSha256 char(64) NOT NULL,
        Revision int NOT NULL,
        ReviewedBy nvarchar(128) NOT NULL,
        ReviewedAtUtc datetime2(7) NOT NULL,
        PayloadJson nvarchar(max) NOT NULL,
        CONSTRAINT PK_CorporationTaxReview
            PRIMARY KEY (TenantReference, LogicalFilingIdentity),
        CONSTRAINT CK_CorporationTaxReview_Period
            CHECK (PeriodEnd >= PeriodStart AND PeriodEnd < DATEADD(year, 1, PeriodStart)),
        CONSTRAINT CK_CorporationTaxReview_Revision CHECK (Revision > 0),
        CONSTRAINT CK_CorporationTaxReview_PayloadJson CHECK (ISJSON(PayloadJson) = 1)
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1
      FROM sys.indexes
     WHERE object_id = OBJECT_ID(N'Tax.CorporationTaxReview')
       AND name = N'IX_CorporationTaxReview_AccountsPeriod'
)
    CREATE INDEX IX_CorporationTaxReview_AccountsPeriod
        ON Tax.CorporationTaxReview
            (TenantReference, AccountsSnapshotSha256, PeriodStart, PeriodEnd);
GO

IF DATABASE_PRINCIPAL_ID(N'TaxHubCorporationTaxApplication') IS NULL
    CREATE ROLE TaxHubCorporationTaxApplication AUTHORIZATION dbo;
GO

IF DATABASE_PRINCIPAL_ID(N'TaxHubCompaniesHouseApplication') IS NOT NULL
    REVOKE SELECT, INSERT, UPDATE ON OBJECT::Tax.CorporationTaxReview
        FROM TaxHubCompaniesHouseApplication;
GO

IF DATABASE_PRINCIPAL_ID(N'TaxHubCorporationTaxApplication') IS NOT NULL
    GRANT SELECT, INSERT, UPDATE ON OBJECT::Tax.CorporationTaxReview
        TO TaxHubCorporationTaxApplication;
GO

IF DATABASE_PRINCIPAL_ID(N'tradecontrol_tax_workflow_runtime') IS NOT NULL
    GRANT SELECT, INSERT, UPDATE ON OBJECT::Tax.CorporationTaxReview
        TO tradecontrol_tax_workflow_runtime;
GO
