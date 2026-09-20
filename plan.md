1. **Prevent Repetitive CID Finding and Pinning attempts**
   - In `Services/WindowsSearchService.cs`, add a caching dictionary `_processedPendingRootTxIds` (e.g., `private readonly ConcurrentDictionary<string, byte> _processedPendingRootTxIds = new(StringComparer.OrdinalIgnoreCase);`) to track processed transactions.
   - Update `StartPendingRootCidPinWorker` (around line 407 in `Services/WindowsSearchService.cs`) to skip processing if `txId` is already present in `_processedPendingRootTxIds` (and store it there when starting the worker). This stops the repetitive `CID-FOUND` logging for the same pending transaction.

2. **Fix Missing File Attachment Parsing**
   - In `Services/WindowsSearchService.cs`'s `ExtractPendingRootIpfsCids` (around line 590), the `File` property is confirmed to be an object mapping filenames to their attachment content (as seen in `IsSystemRoot`). Iterate through all keys in the `File` property of the `document` root JSON object to find all attached files. Currently, it only specifically checks `PRO` and `OBJ` by calling `AddAttachmentFileCidFindings(txId, "PRO", sourceToCids)` and `AddAttachmentFileCidFindings(txId, "OBJ", sourceToCids)`. We will loop over all property names within `document.RootElement.GetProperty("File")` instead.
   - Update `sourceLabel` logic in `TryPinPendingRootIpfsCidsWithRetriesAsync` (around line 437) to use the file name directly rather than defaulting to "Unknown" for non-PRO/OBJ/Message files (i.e. just use `sourceGroup.Key`).

3. **UI Enhancements: Add Root Data Link to Message Cards**
   - In `wwwroot/index.html`, locate `renderMessageCard` around line 2002. `txidHtml` is assigned around here: `const txidHtml=normalized.txId?` <span class="txid-copy" data-txid="${escapeHtml(normalized.txId)}" title="Copy TX ID">📋 ${escapeHtml(normalized.txId.slice(0,8))}…</span>`:''`.
   - Modify the `txidHtml` variable to add a magnifying glass hyperlink `🔍` pointing to `/root/{txId}` right after the transaction ID text.
   - Example HTML snippet: `...<a href="/root/${escapeHtml(normalized.txId)}" title="View Root" style="text-decoration:none; margin-left:4px;">🔍</a>`

4. **Verify Build and Tests**
   - Run `dotnet build` to verify the code compiles and `dotnet test` (if applicable) to ensure tests pass.
   - Use browser or verification tools if necessary to verify frontend changes.

5. **Complete pre commit steps**
   - Complete pre-commit steps to ensure proper testing, verification, review, and reflection are done.

6. **Submit the change.**
   - Submit the change with a descriptive commit message.
