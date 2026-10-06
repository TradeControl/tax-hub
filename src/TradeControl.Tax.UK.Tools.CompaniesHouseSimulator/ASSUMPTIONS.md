# Companies House simulator rule and assumption ledger

## Published-contract rules used by Block A

- GovTalk routes accounts filing with Class `AA`, status requests with `GetSubmissionStatus`, and status acknowledgement with `StatusAck`.
- Accounts use `FormSubmission` and carry an `ACCOUNTS` document with content type `application/xml`.
- `SubmissionNumber` contains six characters.
- Published status values are `PENDING`, `ACCEPT`, `REJECT`, `PARKED` and `INTERNAL_FAILURE`.
- A specific-submission poll does not require `StatusAck`; a general presenter poll does.
- An unacknowledged general-poll result can be returned again.

## Simulator choices requiring official reconciliation

- Authentication is supplied out of band as `SimulatorAuthentication`; Phase 5.12 will own real GovTalk credential materialisation.
- Synthetic authentication and references use the `SIM-` prefix and are never authority credentials.
- Transaction identifiers come from an injected deterministic reference generator; this is a repeatability mechanism, not evidence of authority allocation rules.
- A scenario advances by one state after each specific-submission poll.
- General polls return terminal, unacknowledged statuses ordered by submission number.
- One empty `StatusAck` acknowledges the complete last general-poll batch for the authenticated synthetic presenter. Another presenter cannot inspect or acknowledge that batch.
- Exact request and decoded accounts bytes remain memory-only and can be retrieved only using matching synthetic presenter authentication.
- Simulator acknowledgements and errors use simulator-only bodies because public evidence does not establish a complete current accounts acknowledgement golden.
- `REJECT` and `INTERNAL_FAILURE` are terminal. `PENDING` and `PARKED` are not terminal.

Every item in the second list must yield to observed Companies House developer-test evidence.
