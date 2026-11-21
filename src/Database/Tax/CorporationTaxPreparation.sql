IF SCHEMA_ID(N'Tax') IS NULL
    EXEC(N'CREATE SCHEMA Tax AUTHORIZATION dbo;');
GO

IF OBJECT_ID(N'Tax.CorporationTaxPreparation', N'U') IS NULL
BEGIN
    CREATE TABLE Tax.CorporationTaxPreparation
    (
        TenantReference uniqueidentifier NOT NULL,
        Reference nvarchar(64) NOT NULL,
        AccountsSnapshotSha256 char(64) NOT NULL,
        SourceSha256 char(64) NOT NULL,
        ReviewedInputSha256 char(64) NOT NULL,
        ReturnDetailsSha256 char(64) NOT NULL,
        PreparedBy nvarchar(256) NOT NULL,
        PreparedAtUtc datetime2(7) NOT NULL,
        PayloadJson nvarchar(max) NOT NULL,
        CONSTRAINT PK_Tax_CorporationTaxPreparation
            PRIMARY KEY (TenantReference, Reference),
        CONSTRAINT CK_Tax_CorporationTaxPreparation_PayloadJson
            CHECK (ISJSON(PayloadJson) = 1)
    );

    CREATE INDEX IX_Tax_CorporationTaxPreparation_Latest
        ON Tax.CorporationTaxPreparation
            (TenantReference, AccountsSnapshotSha256, PreparedAtUtc DESC);
END;
GO

IF DATABASE_PRINCIPAL_ID(N'TaxHubCompaniesHouseApplication') IS NOT NULL
BEGIN
    GRANT SELECT, INSERT ON Tax.CorporationTaxPreparation
        TO TaxHubCompaniesHouseApplication;
END;
GO

IF DATABASE_PRINCIPAL_ID(N'tradecontrol_tax_workflow_runtime') IS NOT NULL
BEGIN
    GRANT SELECT, INSERT ON Tax.CorporationTaxPreparation
        TO tradecontrol_tax_workflow_runtime;
END;
GO
