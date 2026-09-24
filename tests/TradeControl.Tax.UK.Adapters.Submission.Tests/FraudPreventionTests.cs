using System.Net;
using TradeControl.Tax.UK.Adapters.Submission.FraudPrevention;
using TradeControl.Tax.UK.Application.Preparation;

internal static class FraudPreventionTests
{
    public static async Task<int> RunAsync(string root)
    {
        var assertions = 0;
        void Assert(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }
        async Task AssertRejectedAsync(Func<Task> action, string message)
        {
            assertions++;
            try { await action(); }
            catch (ArgumentException) { return; }
            catch (FraudContextRejectedException) { return; }
            throw new InvalidOperationException(message);
        }

        var time = new FraudTimeProvider(new DateTimeOffset(2026, 9, 24, 14, 30, 5, 123, TimeSpan.Zero));
        var key = Enumerable.Range(80, 32).Select(value => (byte)value).ToArray();
        var direct = FraudDeploymentTopology.Direct("local-direct-reference", IPAddress.Parse("8.8.4.4"));
        var vendor = Vendor();
        var storeRoot = Path.Combine(root, "fraud-contexts");
        var identity = new FraudActorIdentity("tenant-a", "principal-a", "actor-a");
        var browser = Browser(time.GetUtcNow());
        var ingress = new TrustedIngressConnectionObservation(IPAddress.Parse("1.1.1.1"), 54321, time.GetUtcNow());

        SealedFraudContextReference reference;
        using (var service = FraudHeaderService.CreateFileBacked(storeRoot, key, direct, vendor, clock: time))
        {
            reference = await service.CaptureAndSealAsync(identity, browser, ingress);
            var dispatch = Dispatch(identity, reference);
            var headers = await service.BuildHeadersAsync(dispatch);
            Assert(headers.Count == 16
                && headers["Gov-Client-Connection-Method"] == "WEB_APP_VIA_SERVER"
                && headers["Gov-Client-Public-IP"] == "1.1.1.1"
                && headers["Gov-Client-Public-Port"] == "54321"
                && headers["Gov-Client-Public-IP-Timestamp"] == "2026-09-24T14:30:05.123Z",
                "The complete direct web-app-via-server header set was not formatted exactly.");
            Assert(headers["Gov-Vendor-Forwarded"] == "by=8.8.4.4&for=1.1.1.1"
                && headers["Gov-Vendor-Public-IP"] == "8.8.4.4",
                "The direct public TLS hop was not represented consistently.");
            Assert(headers["Gov-Client-Screens"] ==
                "width=1920&height=1080&scaling-factor=1.25&colour-depth=24",
                "A fractional browser scaling factor was not preserved in invariant decimal form.");
            Assert(headers["Gov-Client-User-IDs"].Contains("tax-hub=alice%2Btest%40example.com", StringComparison.Ordinal)
                && headers["Gov-Vendor-Product-Name"] == "Trade%20Control%20Tax%20Hub"
                && headers.Values.All(value => value.All(character => character <= 127 && !char.IsControl(character))),
                "Fraud header values were not percent encoded into US-ASCII.");
            Assert(!headers.ToString().Contains("alice", StringComparison.Ordinal)
                && !browser.ToString().Contains("Mozilla", StringComparison.Ordinal)
                && !reference.ToString().Contains(reference.Value, StringComparison.Ordinal),
                "A routine fraud-fact diagnostic representation exposed protected evidence.");

            await AssertRejectedAsync(() => service.BuildHeadersAsync(Dispatch(
                new("tenant-b", "principal-a", "actor-a"), reference)),
                "A sealed fraud context crossed a tenant boundary.");
            await AssertRejectedAsync(() => service.BuildHeadersAsync(Dispatch(
                new("tenant-a", "principal-a", "actor-b"), reference)),
                "A sealed fraud context crossed an actor boundary.");
            await AssertRejectedAsync(() => service.CaptureAndSealAsync(identity, browser,
                ingress with { ForwardedClient = new(IPAddress.Parse("9.9.9.9"), 45678) }),
                "A direct deployment accepted spoofed forwarded client data.");
            await AssertRejectedAsync(() => service.CaptureAndSealAsync(identity, browser,
                ingress with { RemoteAddress = IPAddress.Loopback }),
                "A loopback address was recorded as a public client address.");
            await AssertRejectedAsync(() => service.CaptureAndSealAsync(identity, browser,
                ingress with { RemoteAddress = IPAddress.Parse("203.0.113.6") }),
                "A documentation-only address was recorded as a public client address.");
            await AssertRejectedAsync(() => service.CaptureAndSealAsync(identity, browser,
                ingress with { RemotePort = 443 }),
                "A server port was recorded as the client's originating public port.");

            var encryptedText = string.Join('\n', Directory.GetFiles(storeRoot, "*.fraud.enc", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
            Assert(!new[] { "tenant-a", "principal-a", "actor-a", "alice", "Mozilla", "1.1.1.1", "54321" }
                    .Any(value => encryptedText.Contains(value, StringComparison.Ordinal)),
                "Protected fraud evidence was persisted in plaintext.");
        }

        using (var restarted = FraudHeaderService.CreateFileBacked(storeRoot, key, direct, vendor, clock: time))
        {
            var headers = await restarted.BuildHeadersAsync(Dispatch(identity, reference));
            Assert(headers.Count == 16, "A sealed fraud context did not survive service restart.");
            time.Advance(TimeSpan.FromMinutes(16));
            await AssertRejectedAsync(() => restarted.BuildHeadersAsync(Dispatch(identity, reference)),
                "A stale fraud context remained usable for an HMRC request.");
        }

        time.Set(new DateTimeOffset(2026, 9, 24, 15, 0, 0, TimeSpan.Zero));
        var proxy = FraudDeploymentTopology.TrustedProxyChain("reviewed-two-hop-reference",
            new HashSet<IPAddress> { IPAddress.Parse("10.0.0.7") },
            [IPAddress.Parse("8.8.4.4"), IPAddress.Parse("9.9.9.9")]);
        var proxyRoot = Path.Combine(root, "proxy-fraud-contexts");
        using (var proxied = FraudHeaderService.CreateFileBacked(proxyRoot, key, proxy, vendor, clock: time))
        {
            var proxyIngress = new TrustedIngressConnectionObservation(IPAddress.Parse("10.0.0.7"), 8443,
                time.GetUtcNow(), new(IPAddress.Parse("1.0.0.1"), 61234));
            var proxyReference = await proxied.CaptureAndSealAsync(identity, Browser(time.GetUtcNow()), proxyIngress);
            var headers = await proxied.BuildHeadersAsync(Dispatch(identity, proxyReference));
            Assert(headers["Gov-Client-Public-IP"] == "1.0.0.1"
                && headers["Gov-Vendor-Forwarded"] ==
                    "by=8.8.4.4&for=1.0.0.1,by=9.9.9.9&for=8.8.4.4",
                "The configured trusted proxy hops were not formatted in client-to-server order.");
            await AssertRejectedAsync(() => proxied.CaptureAndSealAsync(identity, Browser(time.GetUtcNow()),
                proxyIngress with { RemoteAddress = IPAddress.Parse("10.0.0.8") }),
                "An untrusted immediate peer supplied forwarded client facts.");
            await AssertRejectedAsync(() => proxied.CaptureAndSealAsync(identity, Browser(time.GetUtcNow()),
                proxyIngress with { ForwardedClient = null }),
                "A trusted-proxy deployment silently fell back to the socket peer.");

            var evidenceFile = Directory.GetFiles(proxyRoot, "*.fraud.enc", SearchOption.AllDirectories).Single();
            var bytes = await File.ReadAllBytesAsync(evidenceFile);
            bytes[^1] ^= 0x01;
            await File.WriteAllBytesAsync(evidenceFile, bytes);
            await AssertRejectedAsync(() => proxied.BuildHeadersAsync(Dispatch(identity, proxyReference)),
                "Tampered fraud evidence passed authenticated decryption.");
        }

        var ipv6Root = Path.Combine(root, "ipv6-fraud-contexts");
        var ipv6Topology = FraudDeploymentTopology.Direct("ipv6-direct-reference",
            IPAddress.Parse("2001:4860:4860::8888"));
        using (var ipv6 = FraudHeaderService.CreateFileBacked(ipv6Root, key, ipv6Topology, vendor, clock: time))
        {
            var ipv6Reference = await ipv6.CaptureAndSealAsync(identity, Browser(time.GetUtcNow()),
                new TrustedIngressConnectionObservation(IPAddress.Parse("2606:4700:4700::1111"), 54321,
                    time.GetUtcNow()));
            var headers = await ipv6.BuildHeadersAsync(Dispatch(identity, ipv6Reference));
            Assert(headers["Gov-Vendor-Forwarded"] ==
                "by=2001%3A4860%3A4860%3A%3A8888&for=2606%3A4700%3A4700%3A%3A1111",
                "IPv6 forwarded-hop values were not percent encoded while preserving their separators.");
        }

        using (var strictMissingMfa = FraudHeaderService.CreateFileBacked(
            Path.Combine(root, "missing-mfa"), key, direct, vendor, clock: time))
        {
            var missing = new BrowserFraudFacts("agent", Guid.NewGuid(), [],
                [new(1920, 1080, 1m, 24)], "UTC+00:00",
                new Dictionary<string, string> { ["user"] = "a" }, new(1000, 800));
            var missingReference = await strictMissingMfa.CaptureAndSealAsync(identity, missing,
                new TrustedIngressConnectionObservation(IPAddress.Parse("1.1.1.1"), 54321, time.GetUtcNow()));
            await AssertRejectedAsync(() => strictMissingMfa.BuildHeadersAsync(Dispatch(identity, missingReference)),
                "Missing MFA evidence was accepted by the strict submission formatter.");
        }
        using (var strictMissingLicense = FraudHeaderService.CreateFileBacked(
            Path.Combine(root, "missing-license"), key, direct,
            new FraudVendorConfiguration("product", new Dictionary<string, string> { ["app"] = "1.0" },
                new Dictionary<string, string>()), clock: time))
        {
            var missingReference = await strictMissingLicense.CaptureAndSealAsync(identity, Browser(time.GetUtcNow()),
                new TrustedIngressConnectionObservation(IPAddress.Parse("1.1.1.1"), 54321, time.GetUtcNow()));
            await AssertRejectedAsync(() => strictMissingLicense.BuildHeadersAsync(Dispatch(identity, missingReference)),
                "Missing vendor-license evidence was accepted by the strict submission formatter.");
        }

        var diagnosticTopology = FraudDeploymentTopology.SandboxValidatorDiagnosticDirect(
            "localhost-validator-observation", IPAddress.IPv6Loopback);
        var diagnosticVendor = new FraudVendorConfiguration("Trade Control Tax Hub",
            new Dictionary<string, string> { ["tax-hub-web-harness"] = "5.3.0" },
            new Dictionary<string, string>());
        using (var diagnostic = FraudHeaderService.CreateFileBackedSandboxValidatorDiagnostic(
            Path.Combine(root, "validator-diagnostic"), key, diagnosticTopology, diagnosticVendor, clock: time))
        {
            var observedBrowser = new BrowserFraudFacts("Mozilla/5.0", Guid.NewGuid(), [],
                [new(1920, 1080, 1.25m, 24)], "UTC+01:00", new Dictionary<string, string>(),
                new(1256, 803));
            var observedReference = await diagnostic.CaptureAndSealAsync(identity, observedBrowser,
                new TrustedIngressConnectionObservation(IPAddress.IPv6Loopback, 54321, time.GetUtcNow()));
            var headers = await diagnostic.BuildHeadersAsync(Dispatch(identity, observedReference));
            Assert(headers["Gov-Client-Public-IP"] == "::1"
                && headers["Gov-Vendor-Forwarded"] == "by=%3A%3A1&for=%3A%3A1"
                && headers["Gov-Client-Multi-Factor"] == string.Empty
                && headers["Gov-Client-User-IDs"] == string.Empty
                && headers["Gov-Vendor-License-IDs"] == string.Empty,
                "The sandbox diagnostic did not preserve the actual localhost and missing-session observations.");
        }

        return assertions;
    }

    private static BrowserFraudFacts Browser(DateTimeOffset now) => new(
        "Mozilla/5.0 TaxHub/1.0", Guid.Parse("beec798b-b366-47fa-b1f8-92cede14a1ce"),
        [new(FraudMultiFactorType.AuthorisationCode, now.AddMinutes(-2), new string('B', 64))],
        [new(1920, 1080, 1.25m, 24)], "UTC+01:00",
        new Dictionary<string, string> { ["tax-hub"] = "alice+test@example.com" }, new(1256, 803));

    private static FraudVendorConfiguration Vendor() => new("Trade Control Tax Hub",
        new Dictionary<string, string> { ["tax-hub"] = "5.3.0" },
        new Dictionary<string, string> { ["trade-control"] = new string('A', 64) });

    private static AuthorityDispatchContext Dispatch(FraudActorIdentity identity,
        SealedFraudContextReference reference) => new(identity.TenantReference, identity.PrincipalReference,
        identity.ActorReference, "approval", reference.Value);

    private sealed class FraudTimeProvider(DateTimeOffset value) : TimeProvider
    {
        private DateTimeOffset _value = value;
        public override DateTimeOffset GetUtcNow() => _value;
        public void Advance(TimeSpan duration) => _value += duration;
        public void Set(DateTimeOffset value) => _value = value;
    }
}
