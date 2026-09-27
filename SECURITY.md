# Security policy

## Scope

This project flashes firmware and Windows onto a phone. The most serious issues
are ones that could brick a device, corrupt the EFS/modem backups needed to
return to stock, or cause the installer to write to the wrong partition or the
wrong device.

## Reporting a vulnerability

Please report security issues privately using GitHub's **"Report a
vulnerability"** feature (Security → Advisories) on this repository, rather than
opening a public issue. Include:

- what you observed and how to reproduce it,
- the device model and firmware, and the installer / firmware / driver version,
- the impact you think it has.

We will acknowledge the report, investigate, and coordinate a fix and
disclosure with you.

## Supported versions

Only the latest release is supported. This is a hobbyist project targeting a
single device (SM-G965F) and build; there is no long-term support branch.

## Safety, not just security

Because a mistake here can be destructive, the installer is deliberately
conservative: it verifies the device before every destructive step and refuses
to run stages that are not yet validated. If you find a case where it would act
outside those guardrails, treat it as a security report.
