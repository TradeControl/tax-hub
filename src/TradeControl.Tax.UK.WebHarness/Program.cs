//https://localhost:7277/swagger/index.html


using TradeControl.Tax.UK.Adapters.TradeControl.Data;
using TradeControl.Tax.UK.Adapters.Submission.Audit;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Mapping;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Payloads;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Company;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Runner;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Validation;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Preparation;
using TradeControl.Tax.UK.Application.Preparation;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Tax Hub WebHarness API", Version = "v1" });
});

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
builder.Services.AddSingleton<CorporationTaxPayloadValidator>();
builder.Services.AddSingleton<CorporationTaxRunner>();

var app = builder.Build();

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
    var problem = PreparedRequestProblem.FromException(
        feature?.Error ?? new InvalidOperationException(), context.TraceIdentifier);
    context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
    await context.Response.WriteAsJsonAsync(problem);
}));
app.UseRouting();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Tax Hub WebHarness API v1");
    });
}

app.MapGet("/", () => Results.Redirect("/swagger"));

app.UseAuthorization();
app.MapControllers();

app.Run();
