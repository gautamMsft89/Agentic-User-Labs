# call_function

Use an actually advertised GET/functionUrl contract for supported functions,
not an arbitrary JSON-body action. The name alone does not establish offered
parameters or paths. Guarded/legacy read-only contexts may not offer it.

Examples of conditional function addressing are drive path-based lookup
and channel-message delta. Escape OData literals and URL path components
correctly; do not invent unsupported query selectors. Delta/continuation
capability must be established from actual results, not assumed from a path.
No direct requests to external nextLink URLs.

Actions with request bodies belong to the actual supported action descriptor,
not an invented function wrapper. Schema availability does not grant access.
On denial, stop with the observed limitation; no identity/API-version switch
or alternate path to evade access controls.
