'use strict';

// Compatibility shim for older Chromium-based engines (e.g. Android System
// WebView 80 and Edge 44 / Chromium 77). Loaded before the app scripts so the
// display and control pages can still render when newer APIs are unavailable.

(function compat() {
  const hasOwn = (object, key) => Object.prototype.hasOwnProperty.call(object, key);

  if (!Object.hasOwn) Object.hasOwn = hasOwn;

  if (!Array.prototype.at) {
    Array.prototype.at = function at(offset) {
      const index = Math.trunc(offset) || 0;
      const adjusted = index < 0 ? index + this.length : index;
      return adjusted >= 0 && adjusted < this.length ? this[adjusted] : undefined;
    };
  }

  if (!Array.prototype.findLast) {
    Array.prototype.findLast = function findLast(predicate, thisArg) {
      for (let index = this.length - 1; index >= 0; index--) {
        if (predicate.call(thisArg, this[index], index, this)) return this[index];
      }
      return undefined;
    };
  }

  if (typeof String.prototype.replaceAll !== 'function') {
    String.prototype.replaceAll = function replaceAll(search, replacement) {
      const source = search instanceof RegExp
        ? search.source
        : String(search).replace(/[.*+?^\${}()|[\]\\]/g, '\\$&');
      return this.replace(new RegExp(source, 'g'), replacement);
    };
  }

  if (!Element.prototype.replaceChildren) {
    Element.prototype.replaceChildren = function replaceChildren(...nodes) {
      while (this.firstChild) this.removeChild(this.firstChild);
      for (const node of nodes) {
        if (node != null) this.appendChild(node);
      }
    };
  }

  // Older engines do not expose Element.animate's introspection API. Returning
  // an empty list keeps layout intact and simply skips motion restoration.
  if (!Element.prototype.getAnimations) {
    Element.prototype.getAnimations = function getAnimations() {
      return [];
    };
  }

  if (typeof AbortSignal.timeout !== 'function') {
    AbortSignal.timeout = function timeout(milliseconds) {
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), milliseconds);
      controller.signal.addEventListener('abort', () => clearTimeout(timer), { once: true });
      return controller.signal;
    };
  }

  if (typeof globalThis.structuredClone !== 'function') {
    globalThis.structuredClone = function structuredClone(value) {
      return JSON.parse(JSON.stringify(value));
    };
  }

  // Keep the CSS fallback sheet scoped to engines that cannot do the modern
  // layout primitives. Via 7.x on Android 9 still uses WebView 80.
  try {
    const supports = (property, value) =>
      typeof CSS !== 'undefined' && CSS.supports && CSS.supports(property, value);
    if (!supports('aspect-ratio', '1') || !supports('width', '1cqw'))
      document.documentElement.classList.add('legacy-layout');
  } catch (error) {
    // Feature detection is best-effort; leave the modern layout in place.
  }
})();
