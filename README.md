# Tax Hub (Trade Control)

This repository contains the technical implementation of the UK Tax Hub for
Trade Control. It provides isolated statutory contracts, application workflows,
Trade Control adapters and authority-submission adapters.

The module provides:

- HMRC MTD Income Tax contracts and enquiries
- HMRC MTD VAT contracts and submissions
- Limited Company statutory accounts contracts
- Companies House accounts filing contracts
- HMRC Corporation Tax contracts
- offline contract validation and diagnostic tooling

Product specifications and implementation designs are maintained in the parent
[`tradecontrol.web`](https://github.com/TradeControl/tradecontrol.web) repository
under `docs/projects/Tax Hub`. This repository contains only technical material
owned by the Tax Hub implementation.

Tax Hub is included in `tradecontrol.web` as the `src/tax-hub` Git submodule.

## Repository Status

The repository is being organised around explicit contract, application and
adapter boundaries before Limited Company contract implementation begins.

## Target Framework

Contract, application and adapter libraries currently target `net8.0`. The
diagnostic ASP.NET Core WebHarness currently targets `net9.0`.

## Solution

Open `src/TaxHub.slnx` to build the standalone Tax Hub solution. The parent
`tradecontrol.web` solution loads the WebHarness as the diagnostic composition
root while retaining access to the other Trade Control repositories and product
documentation.

## Build & Runtime

The WebHarness is diagnostic only. Production integration with Trade Control
will use the application and adapter boundaries defined by the parent
repository's approved Tax Hub architecture.

## Licence

The Trade Control Code licence is issued by Trade Control Ltd under a [GNU General Public Licence v3.0](https://www.gnu.org/licenses/gpl-3.0.en.html)
