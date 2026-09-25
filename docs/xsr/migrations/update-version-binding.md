# Update candidate and package identity

Discovery binds the requested release tag, the advertised candidate version, every fetched
index target (including intermediate hops), and the final package version by parsed version
identity. Stable legacy spellings such as `v1.4.11-release` remain equivalent to `1.4.11`.
Mismatched candidate/tag pairs are refused before network access, including portable and
pre-block-updater full-package paths. An index with mismatched tag/version metadata cannot
authorize a patch or change the full fallback's target. The planner checks its own inputs,
and discovery rechecks final eligibility. Different CI commit versions retain the existing
explicit CI-hop rule; moving aliases without a parseable immutable version cannot establish
package identity and are not eligible through this API.
