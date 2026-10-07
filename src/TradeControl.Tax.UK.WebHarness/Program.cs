//https://localhost:7277/swagger/index.html


using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using TradeControl.Tax.UK.Adapters.TradeControl.Data;
using TradeControl.Tax.UK.WebHarness.Controllers;
using TradeControl.Tax.UK.Adapters.Submission.Audit;
using TradeControl.Tax.UK.Adapters.Submission.CompaniesHouse;
using TradeControl.Tax.UK.Adapters.Submission.Configuration;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Mapping;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Payloads;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Company;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Runner;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Validation;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Preparation;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Hmrc;
using TradeControl.Tax.UK.Application.Preparation;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddMemoryCache();
builder.Services.Configure<WebHarnessAuthenticationOptions>(
    builder.Configuration.GetSection(WebHarnessAuthenticationOptions.SectionName));
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.Name = "TaxHub.FraudDiagnostic";
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.IdleTimeout = TimeSpan.FromMinutes(15);
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Tax Hub WebHarness API", Version = "v1" });
});

builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = WebHarnessAuthenticationOptions.ApplicationScheme;
        options.DefaultChallengeScheme = WebHarnessAuthenticationOptions.ApplicationScheme;
        options.DefaultSignOutScheme = WebHarnessAuthenticationOptions.ApplicationScheme;
    })
    .AddCookie(WebHarnessAuthenticationOptions.ApplicationScheme, options =>
    {
        options.Cookie.Name = ".TradeControl.Identity";
        options.Cookie.HttpOnly = true;
        options.Cookie.IsEssential = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.LoginPath = "/diagnostics/hmrc/sign-in";
        options.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers["X-TaxHub-Sign-In"] = "/diagnostics/hmrc/sign-in";
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

if (builder.Environment.IsDevelopment() && OperatingSystem.IsWindows())
{
    var sharedKeyRoot = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath,
        "../../../../.local/tax-hub/shared-identity-keys"));
    Directory.CreateDirectory(sharedKeyRoot);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(sharedKeyRoot))
        .ProtectKeysWithDpapi()
        .SetApplicationName("TradeControl.Web");
}

builder.Services.AddSingleton<ConnectionFactory>();
builder.Services.AddSingleton<PreparedApiRequestPipeline>();
builder.Services.AddSingleton<BodylessRequestDescriber>();
builder.Services.AddSingleton(sp => new PreparedApiRequestStore(
    PreparedApiRequestStoreOptions.Development(
        sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath,
        sp.GetRequiredService<IConfiguration>())));
builder.Services.AddSingleton<SubmissionLogger>();
builder.Services.AddSingleton<TagMapper>();
builder.Services.AddSingleton<CategoryMapper>();
builder.Services.AddSingleton<VatHarnessPayloadBuilder>();
builder.Services.AddSingleton<MicroHarnessPayloadBuilder>();
builder.Services.AddSingleton<VatValidator>();
builder.Services.AddSingleton<MicroValidator>();
builder.Services.AddSingleton<ObligationValidator>();
builder.Services.AddSingleton<SubmissionHistoryValidator>();
builder.Services.AddSingleton<LiabilityValidator>();
builder.Services.AddSingleton<PaymentValidator>();
builder.Services.AddSingleton<HmrcSubmissionRunner>();
builder.Services.AddSingleton<CompanyAccountsPayloadValidator>();
builder.Services.AddSingleton<CompanyAccountsRunner>();
builder.Services.AddSingleton<CompaniesHouseAccountsPayloadValidator>();
builder.Services.AddSingleton<CompaniesHouseAccountsRunner>();
builder.Services.AddSingleton<ICompaniesHouseSubmissionEventLog, CompaniesHouseSubmissionEventLog>();
builder.Services.AddSingleton<ICompaniesHouseAccountsRunner>(sp =>
    sp.GetRequiredService<CompaniesHouseAccountsRunner>());
builder.Services.AddSingleton<ICompaniesHouseDiagnosticGatewayFactory>(sp =>
{
    var mode = builder.Configuration["TaxHub:CompaniesHouse:DiagnosticGateway"]
        ?? throw new InvalidOperationException("A Companies House diagnostic gateway must be selected explicitly.");
    return mode switch
    {
        "Simulator" => new InProcessCompaniesHouseDiagnosticGatewayFactory(),
        "OfficialTestSendDisabled" => new SendDisabledCompaniesHouseOfficialTestGatewayFactory(),
        _ => throw new InvalidOperationException("The Companies House diagnostic gateway selection is unsupported.")
    };
});
builder.Services.AddSingleton<ICompaniesHouseSimulationDiagnostics>(sp =>
    builder.Environment.IsDevelopment()
        ? new CompaniesHouseSimulationDiagnostics(
            sp.GetRequiredService<ICompaniesHouseAccountsRunner>(),
            sp.GetRequiredService<ICompaniesHouseDiagnosticGatewayFactory>())
        : new DisabledCompaniesHouseSimulationDiagnostics());
builder.Services.AddSingleton<ICompaniesHouseOfficialTestPreflight>(sp =>
{
    if (!builder.Environment.IsDevelopment()) return new DisabledCompaniesHouseOfficialTestPreflight();
    var contentRoot = sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath;
    var protectedRoot = Path.GetFullPath(Path.Combine(contentRoot,
        "../../../../.local/companies-house/test-account"));
    var testAccount = new LabelledTextFileSecretProvider(
        Path.Combine(protectedRoot, "credentials.txt"),
        CompaniesHouseSecretReferences.TestAccountFileLabels);
    var companyAuthentication = new LabelledTextFileSecretProvider(
        Path.Combine(protectedRoot, "company-authentication.txt"),
        new Dictionary<SecretReference, string>
        {
            [CompaniesHouseSecretReferences.CompanyAuthentication] = "Company Authentication Code"
        });
    return new CompaniesHouseOfficialTestPreflight(
        sp.GetRequiredService<ICompaniesHouseAccountsRunner>(), testAccount, companyAuthentication);
});
builder.Services.AddSingleton<ICompaniesHouseOfficialTestSubmission>(sp =>
{
    if (!builder.Environment.IsDevelopment()
        || !builder.Configuration.GetValue<bool>("TaxHub:CompaniesHouse:OfficialTestSendEnabled"))
        return new DisabledCompaniesHouseOfficialTestSubmission();
    var contentRoot = sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath;
    var protectedRoot = Path.GetFullPath(Path.Combine(contentRoot,
        "../../../../.local/companies-house/test-account"));
    var testAccount = new LabelledTextFileSecretProvider(
        Path.Combine(protectedRoot, "credentials.txt"),
        CompaniesHouseSecretReferences.TestAccountFileLabels);
    var companyAuthentication = new LabelledTextFileSecretProvider(
        Path.Combine(protectedRoot, "company-authentication.txt"),
        new Dictionary<SecretReference, string>
        {
            [CompaniesHouseSecretReferences.CompanyAuthentication] = "Company Authentication Code"
        });
    var routed = new RoutedSecretProvider(new Dictionary<SecretReference, ISecretProvider>
    {
        [CompaniesHouseSecretReferences.TestPresenterId] = testAccount,
        [CompaniesHouseSecretReferences.TestPresenterAuthentication] = testAccount,
        [CompaniesHouseSecretReferences.TestFlag] = testAccount,
        [CompaniesHouseSecretReferences.TestPackageReference] = testAccount,
        [CompaniesHouseSecretReferences.CompanyAuthentication] = companyAuthentication
    });
    return new CompaniesHouseOfficialTestSubmission(
        sp.GetRequiredService<ICompaniesHouseAccountsRunner>(), routed,
        CompaniesHouseOfficialTestGatewayFactory.Create(),
        sp.GetRequiredService<ICompaniesHouseSubmissionEventLog>(),
        Path.Combine(protectedRoot, "evidence"));
});
builder.Services.AddSingleton<ICompaniesHouseOfficialTestStatus>(sp =>
{
    if (!builder.Environment.IsDevelopment()
        || !builder.Configuration.GetValue<bool>("TaxHub:CompaniesHouse:OfficialTestStatusEnabled"))
        return new DisabledCompaniesHouseOfficialTestStatus();
    var contentRoot = sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath;
    var protectedRoot = Path.GetFullPath(Path.Combine(contentRoot,
        "../../../../.local/companies-house/test-account"));
    var testAccount = new LabelledTextFileSecretProvider(
        Path.Combine(protectedRoot, "credentials.txt"),
        CompaniesHouseSecretReferences.TestAccountFileLabels);
    return new CompaniesHouseOfficialTestStatus(testAccount,
        CompaniesHouseOfficialTestGatewayFactory.Create(),
        Path.Combine(protectedRoot, "evidence"));
});
builder.Services.AddSingleton<CorporationTaxPayloadValidator>();
builder.Services.AddSingleton<CorporationTaxRunner>();
var hmrcSandboxDiagnosticsEnabled = builder.Environment.IsDevelopment()
    || builder.Configuration.GetValue<bool>("TaxHub:HmrcSandbox:Enabled");
builder.Services.AddSingleton<IHmrcSandboxDiagnostics>(sp =>
    hmrcSandboxDiagnosticsEnabled
        ? HmrcSandboxDiagnostics.Create(
            sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath,
            sp.GetRequiredService<IConfiguration>())
        : new DisabledHmrcSandboxDiagnostics());

var app = builder.Build();

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
    var exception = feature?.Error ?? new InvalidOperationException();
    context.RequestServices.GetRequiredService<ILoggerFactory>()
        .CreateLogger("TradeControl.Tax.UK.WebHarness.Unhandled")
        .LogError(exception, "Unhandled WebHarness request failure. CorrelationId: {CorrelationId}",
            context.TraceIdentifier);
    var problem = PreparedRequestProblem.FromException(
        exception, context.TraceIdentifier);
    context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
    await context.Response.WriteAsJsonAsync(problem);
}));
app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.UseAuthentication();

if (hmrcSandboxDiagnosticsEnabled)
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Tax Hub WebHarness API v1");
        c.InjectJavascript("/swagger-fraud.js?v=20261003.1");
    });
}

app.MapGet("/", () => Results.Redirect("/swagger"));

app.UseAuthorization();
app.MapControllers();

app.Run();
