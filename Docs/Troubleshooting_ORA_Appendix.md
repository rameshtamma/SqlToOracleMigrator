# Appendix — ORA issues addressed (reference)

| Error | Cause | Handling | Stage |
|---|---|---|---|
| ORA-00906 | Missing parentheses in type | Fix mappings/templates | Stage 5 |
| ORA-01450 | Index key too long | Skip terminal / remediate | Stage 6 |
| ORA-02327 | LOB/XML expression index | Skip terminal / defer | Stage 6 |
| ORA-00955 | Name exists | Ignore idempotent | Stage 6 |
| ORA-01408 | Already indexed | Ignore idempotent | Stage 6 |
| ORA-02260/02261 | PK/UK exists | Ignore/skip | Stage 6 |
| ORA-00933/00942 | T-SQL view/proc or deps | Defer/skip | Stage 6 |
| ORA-24344 | Invalid compile | Non-blocking + report | Stage 6 |
| ORA-06550/PLS-00103 | Brittle wrapper | Use EXECUTE IMMEDIATE | Stage 6 |
