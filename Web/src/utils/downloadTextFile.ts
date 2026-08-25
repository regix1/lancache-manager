/**
 * Hands the browser a generated text file to save under `filename`.
 *
 * The anchor is put into the document before it is clicked and taken out afterwards. A detached
 * anchor's click is ignored outside Chromium, so an export that works in one browser silently does
 * nothing in another. The object URL is released straight away, since the download has already been
 * handed off by the time click returns.
 */
export function downloadTextFile(content: string, filename: string, mimeType: string): void {
  const blob = new Blob([content], { type: mimeType });
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = filename;
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
  URL.revokeObjectURL(url);
}
