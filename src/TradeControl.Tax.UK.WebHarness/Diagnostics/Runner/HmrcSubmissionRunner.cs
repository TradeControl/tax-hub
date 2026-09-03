using TradeControl.Tax.UK.Adapters.Submission.Audit;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Hmrc;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Payloads;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Validation;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Runner;

public sealed class HmrcSubmissionRunner
{
    private readonly VatValidator _vatValidator;
    private readonly MicroValidator _microValidator;
    private readonly ObligationValidator _obligationValidator;
    private readonly SubmissionHistoryValidator _submissionHistoryValidator;
    private readonly LiabilityValidator _liabilityValidator;
    private readonly PaymentValidator _paymentValidator;
    private readonly VatHarnessPayloadBuilder _vatPayloadBuilder;
    private readonly MicroHarnessPayloadBuilder _microPayloadBuilder;
    private readonly SubmissionLogger _submissionLogger;

    public HmrcSubmissionRunner(
        VatValidator vatValidator,
        MicroValidator microValidator,
        ObligationValidator obligationValidator,
        SubmissionHistoryValidator submissionHistoryValidator,
        LiabilityValidator liabilityValidator,
        PaymentValidator paymentValidator,
        VatHarnessPayloadBuilder vatPayloadBuilder,
        MicroHarnessPayloadBuilder microPayloadBuilder,
        SubmissionLogger submissionLogger)
    {
        _vatValidator = vatValidator;
        _microValidator = microValidator;
        _obligationValidator = obligationValidator;
        _submissionHistoryValidator = submissionHistoryValidator;
        _liabilityValidator = liabilityValidator;
        _paymentValidator = paymentValidator;
        _vatPayloadBuilder = vatPayloadBuilder;
        _microPayloadBuilder = microPayloadBuilder;
        _submissionLogger = submissionLogger;
    }

    public async Task<HmrcSubmissionResult> ExecuteAsync(
        HmrcSubmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<OperationType>(request.OperationType, true, out var operationType))
        {
            return new HmrcSubmissionResult
            {
                Status = "validation_error",
                Errors = new[] { $"Unknown operation type '{request.OperationType}'." }
            };
        }

        var validation = Validate(operationType, request.Parameters);
        if (!validation.IsValid)
        {
            return new HmrcSubmissionResult
            {
                Status = "validation_error",
                Errors = validation.Errors,
                Warnings = validation.Warnings
            };
        }

        try
        {
            var result = operationType switch
            {
                OperationType.SubmitVat => await ExecuteSubmitVatAsync(request.Parameters, cancellationToken),
                OperationType.SubmitMicro => await ExecuteSubmitMicroAsync(request.Parameters, cancellationToken),
                OperationType.GetObligations => BuildNotImplementedEnquiryResult("GET_OBLIGATIONS"),
                OperationType.GetSubmissions => BuildNotImplementedEnquiryResult("GET_SUBMISSIONS"),
                OperationType.GetLiabilities => BuildNotImplementedEnquiryResult("GET_LIABILITIES"),
                OperationType.GetPayments => BuildNotImplementedEnquiryResult("GET_PAYMENTS"),
                _ => throw new InvalidOperationException($"Unsupported operation type '{operationType}'.")
            };

            await _submissionLogger.LogAsync(request.OperationType, result.Status, cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            await _submissionLogger.LogAsync(request.OperationType, "hmrc_error", cancellationToken);

            return new HmrcSubmissionResult
            {
                Status = "hmrc_error",
                Errors = new[] { ex.Message },
                HmrcErrors = new[]
                {
                    new HmrcError
                    {
                        Code = "OBJECTIVE2_EXECUTION_ERROR",
                        Message = ex.Message
                    }
                }
            };
        }
    }

    private ValidationResult Validate(OperationType operationType, Dictionary<string, object?> parameters)
    {
        return operationType switch
        {
            OperationType.SubmitVat => _vatValidator.Validate(parameters),
            OperationType.SubmitMicro => _microValidator.Validate(parameters),
            OperationType.GetObligations => _obligationValidator.Validate(parameters),
            OperationType.GetSubmissions => _submissionHistoryValidator.Validate(parameters),
            OperationType.GetLiabilities => _liabilityValidator.Validate(parameters),
            OperationType.GetPayments => _paymentValidator.Validate(parameters),
            _ => throw new InvalidOperationException($"Unsupported operation type '{operationType}'.")
        };
    }

    private async Task<HmrcSubmissionResult> ExecuteSubmitVatAsync(
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        var payload = await _vatPayloadBuilder.BuildAsync(
            parameters["connectionString"]!.ToString()!,
            parameters["taxSourceCode"]!.ToString()!,
            parameters["subjectId"]!.ToString()!,
            DateTime.Parse(parameters["periodEndOn"]!.ToString()!),
            cancellationToken);

        return BuildSubmissionSuccessResult(payload);
    }

    private async Task<HmrcSubmissionResult> ExecuteSubmitMicroAsync(
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        var payload = await _microPayloadBuilder.BuildAsync(
            parameters["connectionString"]!.ToString()!,
            parameters["taxSourceCode"]!.ToString()!,
            parameters["subjectId"]!.ToString()!,
            DateTime.Parse(parameters["periodTo"]!.ToString()!),
            cancellationToken);

        return BuildSubmissionSuccessResult(payload);
    }

    private static HmrcSubmissionResult BuildSubmissionSuccessResult(object payload)
    {
        return new HmrcSubmissionResult
        {
            Status = "success",
            Payload = payload,
            HmrcResponse = new
            {
                mode = "simulation",
                message = "HMRC submission transport is outside Objective 2 scope."
            },
            SubmissionReference = Guid.NewGuid().ToString("N"),
            SubmittedAt = DateTimeOffset.UtcNow
        };
    }

    private static HmrcSubmissionResult BuildNotImplementedEnquiryResult(string operationName)
    {
        return new HmrcSubmissionResult
        {
            Status = "hmrc_error",
            Errors = new[] { $"{operationName} is outside the implemented Objective 2 submission logic scope." }
        };
    }
}
