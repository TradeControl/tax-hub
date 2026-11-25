SET NOCOUNT ON;

IF SCHEMA_ID(N'Tax') IS NULL EXEC(N'CREATE SCHEMA Tax AUTHORIZATION dbo;');
GO

IF OBJECT_ID(N'Tax.CorporationTaxConversation', N'U') IS NULL
BEGIN
    CREATE TABLE Tax.CorporationTaxConversation
    (
        TenantReference uniqueidentifier NOT NULL,
        Reference nvarchar(64) NOT NULL,
        PrincipalReference nvarchar(256) NOT NULL,
        LogicalSubmissionReference nvarchar(256) NOT NULL,
        StageCode smallint NOT NULL,
        IsComplete bit NOT NULL,
        PollNotBeforeUtc datetime2(7) NULL,
        UpdatedAtUtc datetime2(7) NOT NULL,
        PayloadJson nvarchar(max) NOT NULL,
        CONSTRAINT PK_Tax_CorporationTaxConversation
            PRIMARY KEY (TenantReference, Reference),
        CONSTRAINT CK_Tax_CorporationTaxConversation_Stage
            CHECK (StageCode BETWEEN 0 AND 6),
        CONSTRAINT CK_Tax_CorporationTaxConversation_PayloadJson
            CHECK (ISJSON(PayloadJson) = 1)
    );

    CREATE UNIQUE INDEX UX_Tax_CorporationTaxConversation_LogicalSubmission
        ON Tax.CorporationTaxConversation (TenantReference, LogicalSubmissionReference);

    CREATE INDEX IX_Tax_CorporationTaxConversation_Due
        ON Tax.CorporationTaxConversation
            (IsComplete, StageCode, PollNotBeforeUtc, UpdatedAtUtc)
        INCLUDE (TenantReference, Reference, PrincipalReference);
END;
GO

IF DATABASE_PRINCIPAL_ID(N'TaxHubCorporationTaxApplication') IS NULL
    CREATE ROLE TaxHubCorporationTaxApplication AUTHORIZATION dbo;
GO

REVOKE SELECT, INSERT, UPDATE, DELETE ON OBJECT::Tax.CorporationTaxConversation
    FROM TaxHubCorporationTaxApplication;
GRANT SELECT, INSERT, UPDATE ON OBJECT::Tax.CorporationTaxConversation
    TO TaxHubCorporationTaxApplication;
GO

IF DATABASE_PRINCIPAL_ID(N'tradecontrol_tax_workflow_runtime') IS NOT NULL
BEGIN
    REVOKE SELECT, INSERT, UPDATE, DELETE ON OBJECT::Tax.CorporationTaxConversation
        FROM tradecontrol_tax_workflow_runtime;
    GRANT SELECT, INSERT, UPDATE ON OBJECT::Tax.CorporationTaxConversation
        TO tradecontrol_tax_workflow_runtime;
END;
GO
