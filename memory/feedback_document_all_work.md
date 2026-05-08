---
name: Document all work for future sessions
description: User wants all changes documented in memory so future sessions have full context
type: feedback
---

After completing any phase of work, save comprehensive memory covering:
- What was done and what files were created/modified
- Key decisions and their rationale
- Any gotchas discovered while reading the actual source code
- What phase comes next

**Why:** The user explicitly asked for this ("document everything you do for future sessions") so subsequent Claude sessions don't have to re-derive context from scratch.

**How to apply:** At the end of every work session on this project, update `project_native_rewrite.md` with current phase status, and write any new feedback/project memory files for discoveries made during that session.
