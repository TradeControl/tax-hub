SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF SCHEMA_ID(N'Tax') IS NULL EXEC(N'CREATE SCHEMA Tax AUTHORIZATION dbo;');
GO

IF OBJECT_ID(N'Tax.CorporationTaxApproval', N'U') IS NULL
BEGIN
    CREATE TABLE Tax.CorporationTaxApproval
    (
        TenantReference uniqueidentifier NOT NULL,
        Reference nvarchar(64) NOT NULL,
        PreparationReference nvarchar(64) NOT NULL,
        AccountsSnapshotSha256 char(64) NOT NULL,
        ApprovalSha256 char(64) NOT NULL,
        ApprovedAtUtc datetime2(7) NOT NULL,
        PayloadJson nvarchar(max) NOT NULL,
        CONSTRAINT PK_Tax_CorporationTaxApproval
            PRIMARY KEY (TenantReference, Reference),
        CONSTRAINT FK_Tax_CorporationTaxApproval_Preparation
            FOREIGN KEY (TenantReference, PreparationReference)
            REFERENCES Tax.CorporationTaxPreparation (TenantReference, Reference),
        CONSTRAINT CK_Tax_CorporationTaxApproval_PayloadJson
            CHECK (ISJSON(PayloadJson) = 1)
    );

    CREATE UNIQUE INDEX UX_Tax_CorporationTaxApproval_Preparation
        ON Tax.CorporationTaxApproval (TenantReference, PreparationReference);
END;
GO

IF DATABASE_PRINCIPAL_ID(N'TaxHubCorporationTaxApplication') IS NULL
    CREATE ROLE TaxHubCorporationTaxApplication AUTHORIZATION dbo;
GO

REVOKE SELECT, INSERT, UPDATE, DELETE ON OBJECT::Tax.CorporationTaxApproval
    FROM TaxHubCorporationTaxApplication;
GRANT SELECT, INSERT ON OBJECT::Tax.CorporationTaxApproval
    TO TaxHubCorporationTaxApplication;
GO

IF DATABASE_PRINCIPAL_ID(N'tradecontrol_tax_workflow_runtime') IS NOT NULL
BEGIN
    REVOKE SELECT, INSERT, UPDATE, DELETE ON OBJECT::Tax.CorporationTaxApproval
        FROM tradecontrol_tax_workflow_runtime;
    GRANT SELECT, INSERT ON OBJECT::Tax.CorporationTaxApproval
        TO tradecontrol_tax_workflow_runtime;
END;
GO
