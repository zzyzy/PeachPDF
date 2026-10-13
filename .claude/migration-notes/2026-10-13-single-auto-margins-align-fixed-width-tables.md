# Single auto margins align fixed-width tables

**Before:** A definite-width table with `margin-left: auto` and a non-auto right margin stayed at the containing block's start edge instead of moving to its end edge. A lone `margin-right: auto` was likewise treated as zero. Both-auto margins already centered the table. The v0.9.22 support matrix documented the left-auto limitation.

**Now:** A single auto horizontal margin on a fixed-width table takes the remaining free space after the table's settled width and other margin are accounted for. `margin-left: auto` aligns an LTR table to the end edge; `margin-right: auto` leaves it at the start edge. If the table is wider than its containing block, the auto margin resolves to zero. RTL table placement remains a separate limitation.

**Why:** The table engine resolves the table's used width after sizing its columns, then supplies that width to the shared auto-margin resolver. This lets the resolver apply the remaining-space rule without estimating the table's width earlier in layout.
