SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF SCHEMA_ID(N'Tax') IS NULL EXEC(N'CREATE SCHEMA Tax AUTHORIZATION dbo;');
GO

IF SCHEMA_ID(N'TaxHub') IS NOT NULL
BEGIN
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'TaxHub.CompaniesHousePreparation', N'U') IS NOT NULL
       AND OBJECT_ID(N'Tax.CompaniesHousePreparation', N'U') IS NOT NULL
        THROW 51000, 'Both TaxHub and Tax preparation tables exist.', 1;
    IF OBJECT_ID(N'TaxHub.CompaniesHouseApproval', N'U') IS NOT NULL
       AND OBJECT_ID(N'Tax.CompaniesHouseApproval', N'U') IS NOT NULL
        THROW 51000, 'Both TaxHub and Tax approval tables exist.', 1;
    IF OBJECT_ID(N'TaxHub.CompaniesHouseConversation', N'U') IS NOT NULL
       AND OBJECT_ID(N'Tax.CompaniesHouseConversation', N'U') IS NOT NULL
        THROW 51000, 'Both TaxHub and Tax conversation tables exist.', 1;
    IF OBJECT_ID(N'TaxHub.CompaniesHouseAuthoritySequence', N'U') IS NOT NULL
       AND OBJECT_ID(N'Tax.CompaniesHouseAuthoritySequence', N'U') IS NOT NULL
        THROW 51000, 'Both TaxHub and Tax authority-sequence tables exist.', 1;

    IF OBJECT_ID(N'TaxHub.CompaniesHousePreparation', N'U') IS NOT NULL
        EXEC(N'ALTER SCHEMA Tax TRANSFER TaxHub.CompaniesHousePreparation;');
    IF OBJECT_ID(N'TaxHub.CompaniesHouseApproval', N'U') IS NOT NULL
        EXEC(N'ALTER SCHEMA Tax TRANSFER TaxHub.CompaniesHouseApproval;');
    IF OBJECT_ID(N'TaxHub.CompaniesHouseConversation', N'U') IS NOT NULL
        EXEC(N'ALTER SCHEMA Tax TRANSFER TaxHub.CompaniesHouseConversation;');
    IF OBJECT_ID(N'TaxHub.CompaniesHouseAuthoritySequence', N'U') IS NOT NULL
        EXEC(N'ALTER SCHEMA Tax TRANSFER TaxHub.CompaniesHouseAuthoritySequence;');

    IF EXISTS (SELECT 1 FROM sys.objects WHERE schema_id = SCHEMA_ID(N'TaxHub'))
        THROW 51000, 'The TaxHub schema contains objects outside the Companies House workflow.', 1;

    EXEC(N'DROP SCHEMA TaxHub;');
    COMMIT TRANSACTION;
END;
GO

IF OBJECT_ID(N'Tax.CompaniesHousePreparation', N'U') IS NULL
BEGIN
    CREATE TABLE Tax.CompaniesHousePreparation
    (
        TenantReference uniqueidentifier NOT NULL,
        Reference nvarchar(64) NOT NULL,
        CompanyIdentitySha256 char(64) NOT NULL,
        PeriodEnd date NOT NULL,
        CreatedAtUtc datetime2(7) NOT NULL,
        PayloadJson nvarchar(max) NOT NULL,
        CONSTRAINT PK_CompaniesHousePreparation PRIMARY KEY (TenantReference, Reference),
        CONSTRAINT CK_CompaniesHousePreparation_PayloadJson CHECK (ISJSON(PayloadJson) = 1)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'Tax.CompaniesHousePreparation')
    AND name = N'IX_CompaniesHousePreparation_CompanyPeriod')
    CREATE INDEX IX_CompaniesHousePreparation_CompanyPeriod
        ON Tax.CompaniesHousePreparation (TenantReference, CompanyIdentitySha256, PeriodEnd);
GO

IF OBJECT_ID(N'Tax.CompaniesHouseApproval', N'U') IS NULL
BEGIN
    CREATE TABLE Tax.CompaniesHouseApproval
    (
        TenantReference uniqueidentifier NOT NULL,
        Reference nvarchar(64) NOT NULL,
        PreparationReference nvarchar(64) NOT NULL,
        LogicalFilingIdentity char(64) NOT NULL,
        CompanyIdentitySha256 char(64) NOT NULL,
        PeriodEnd date NOT NULL,
        ApprovedAtUtc datetime2(7) NOT NULL,
        PayloadJson nvarchar(max) NOT NULL,
        CONSTRAINT PK_CompaniesHouseApproval PRIMARY KEY (TenantReference, Reference),
        CONSTRAINT FK_CompaniesHouseApproval_Preparation FOREIGN KEY (TenantReference, PreparationReference)
            REFERENCES Tax.CompaniesHousePreparation (TenantReference, Reference),
        CONSTRAINT CK_CompaniesHouseApproval_PayloadJson CHECK (ISJSON(PayloadJson) = 1)
    );
END;
GO

IF OBJECT_ID(N'Tax.CompaniesHouseConversation', N'U') IS NULL
BEGIN
    CREATE TABLE Tax.CompaniesHouseConversation
    (
        TenantReference uniqueidentifier NOT NULL,
        Reference nvarchar(64) NOT NULL,
        ApprovalReference nvarchar(64) NOT NULL,
        LogicalFilingIdentity char(64) NOT NULL,
        CompanyIdentitySha256 char(64) NOT NULL,
        State smallint NOT NULL,
        IsActive bit NOT NULL,
        UpdatedAtUtc datetime2(7) NOT NULL,
        PayloadJson nvarchar(max) NOT NULL,
        CONSTRAINT PK_CompaniesHouseConversation PRIMARY KEY (TenantReference, Reference),
        CONSTRAINT FK_CompaniesHouseConversation_Approval FOREIGN KEY (TenantReference, ApprovalReference)
            REFERENCES Tax.CompaniesHouseApproval (TenantReference, Reference),
        CONSTRAINT CK_CompaniesHouseConversation_PayloadJson CHECK (ISJSON(PayloadJson) = 1)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'Tax.CompaniesHouseApproval')
    AND name = N'UX_CompaniesHouseApproval_Preparation')
    CREATE UNIQUE INDEX UX_CompaniesHouseApproval_Preparation
        ON Tax.CompaniesHouseApproval (TenantReference, PreparationReference);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'Tax.CompaniesHouseConversation')
    AND name = N'UX_CompaniesHouseConversation_ActiveLogicalFiling')
    CREATE UNIQUE INDEX UX_CompaniesHouseConversation_ActiveLogicalFiling
        ON Tax.CompaniesHouseConversation (TenantReference, LogicalFilingIdentity)
        WHERE IsActive = 1;
GO

IF OBJECT_ID(N'Tax.CompaniesHouseAuthoritySequence', N'U') IS NULL
BEGIN
    CREATE TABLE Tax.CompaniesHouseAuthoritySequence
    (
        AuthorityEnvironment nvarchar(32) NOT NULL,
        LastAllocated int NOT NULL,
        UpdatedAtUtc datetime2(7) NOT NULL,
        CONSTRAINT PK_CompaniesHouseAuthoritySequence PRIMARY KEY (AuthorityEnvironment),
        CONSTRAINT CK_CompaniesHouseAuthoritySequence_Range
            CHECK (LastAllocated BETWEEN 1 AND 99999)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'Tax.CompaniesHouseConversation')
    AND name = N'IX_CompaniesHouseConversation_History')
    CREATE INDEX IX_CompaniesHouseConversation_History
        ON Tax.CompaniesHouseConversation (TenantReference, CompanyIdentitySha256, UpdatedAtUtc DESC);
GO

IF DATABASE_PRINCIPAL_ID(N'TaxHubCompaniesHouseApplication') IS NULL
    CREATE ROLE TaxHubCompaniesHouseApplication AUTHORIZATION dbo;
GO

GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::Tax TO TaxHubCompaniesHouseApplication;
GO
