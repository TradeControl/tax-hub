# HMRC_MTD Module (Trade Control)

This repository contains the implementation of the HMRC Making Tax Digital (MTD)
submission and enquiry module for Trade Control. It replaces the legacy
lockdown‑era prototype and is now aligned with the 2026 specification suite.

The module provides:

- VAT submissions (MTD VAT)
- Micro‑entity submissions
- HMRC enquiry surfaces:
  - obligations
  - submissions
  - liabilities
  - payments

All behaviour, payload structures, validators, and execution rules are defined
in the specification documents located in the `/docs`.

The HMRC_MTD repo is a sub-module of [treadecontrol.web](https://github.com/TradeControl/tradecontrol.web)

## Repository Status

The master branch has been reset for the 2026 implementation.  

This branch is now ready for the coding model to generate the new module
according to the published specifications.

## Target Framework

The HMRC_MTD module is implemented as a .NET class library targeting:

**`net8.0` (Long-Term Support)**

This ensures compatibility with the modern ASP.NET Core ecosystem and aligns
with the Trade Control platform’s forward development path.

## Specification Documents

The following document governs the implementation:

- **tax-hub-spec-programme.md**  
  High‑level specification and delivery plan, located in [treadecontrol.web](https://github.com/TradeControl/tradecontrol.web/blob/HEAD/docs/specs/tax-hub-spec-programme.md).

## Build & Runtime

The module is a standard .NET 8 class library and integrates with Trade Control
through the submission runner and WebHarness API defined in the specification.

## Licence

The Trade Control Code licence is issued by Trade Control Ltd under a [GNU General Public Licence v3.0](https://www.gnu.org/licenses/gpl-3.0.en.html)
