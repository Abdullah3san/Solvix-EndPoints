# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow SemVer once
releases begin.

## [Unreleased]

### Documentation
- Finalized the target architecture for review before Phase 04, based on the product
  decisions of 2026-11-12: `docs/DECISIONS.md` (ADRs, challenges, risks, assumptions,
  open decisions), `docs/DATABASE.md` (MSP tenancy and device data model, partitioning,
  retention, GDPR), `docs/SECURITY.md`, `docs/DEVICE_PLATFORM.md`,
  `docs/INFRASTRUCTURE.md`, `docs/API_DOCUMENTATION.md`, `docs/DEVELOPMENT_ROADMAP.md`.
- `docs/ARCHITECTURE.md` now distinguishes implemented and target architecture.
- Removed `docs/ARCHITECTURE-PROPOSAL.md`; its content is superseded by the documents
  above (notably, mTLS device authentication was replaced by ADR-006).

## Phase 01 – Platform foundation
- Multi-tenant foundation: tenant registration and management, company profile,
  branches, departments, JWT authentication via Next.js BFF, four-layer tenant isolation
  including PostgreSQL Row-Level Security, Docker Compose. See `docs/PHASE-01-SUMMARY.md`.
