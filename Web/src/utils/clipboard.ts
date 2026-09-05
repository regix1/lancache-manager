import { noAutofill } from '@utils/autofill';

/**
 * Copies text, and works on the page this app is usually reached from.
 *
 * `navigator.clipboard` only exists in a secure context, so it is absent on a phone opening
 * `http://<server>:8080` over the LAN, which is the ordinary way this app is used rather than a rare
 * one. A bare `writeText` there throws, and a caller that swallows the throw leaves a copy button
 * that looks broken: nothing is copied and nothing says so.
 *
 * The fallback selects the text in an off-screen field and asks the document to copy the selection,
 * which needs no secure context. `execCommand` is deprecated and still the only thing that works
 * here, so it stays until browsers offer a plain-http path.
 *
 * @returns whether the text reached the clipboard, so the caller can confirm or report it rather
 * than claiming success it did not have.
 */
export async function copyText(text: string): Promise<boolean> {
  if (navigator.clipboard?.writeText) {
    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch {
      // Present but refused, which permission prompts and locked-down browsers both do. The
      // selection path below is still worth trying.
    }
  }

  const field = document.createElement('textarea');
  for (const [name, value] of Object.entries(noAutofill)) {
    field.setAttribute(name, value);
  }
  field.value = text;
  // readonly stops the mobile keyboard opening over the dialog before the copy happens.
  field.setAttribute('readonly', '');
  field.style.position = 'fixed';
  field.style.top = '-9999px';
  field.style.opacity = '0';
  document.body.appendChild(field);

  try {
    field.focus();
    field.select();
    // iOS ignores select() on its own and copies nothing without an explicit range.
    field.setSelectionRange(0, text.length);
    return document.execCommand('copy');
  } catch {
    return false;
  } finally {
    document.body.removeChild(field);
  }
}
