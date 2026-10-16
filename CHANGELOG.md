# Changelog

All notable changes to Aviary are documented here, starting with version 0.4.2.
This file follows Keep a Changelog and uses semantic versioning.

## [0.4.2] - 2026-10-16

### Fixed

- Hyper-V SSH connections recover when the Default Switch subnet changes. Aviary refreshes saved guest addresses every five seconds while open.
- Windows guest SSH setup installs a protected task that updates the host-only firewall rule at startup and every minute. It disables the rule when no valid gateway is available and never recreates a deleted rule.

### Notes

- Existing Windows Hyper-V guests need SSH setup run again to install the gateway refresh task. Subnet changes can take up to a minute to recover. Reopen Aviary to refresh saved addresses if it was closed when the guest address changed.
