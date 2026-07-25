/* Docs site polish: external links open in a new tab; mark active language. */
(function () {
  function isExternal(href) {
    if (!href) {
      return false;
    }
    return /^(https?:)?\/\//i.test(href);
  }

  function enhanceLinks(root) {
    root.querySelectorAll("a[href]").forEach(function (anchor) {
      var href = anchor.getAttribute("href");
      if (!isExternal(href)) {
        return;
      }
      /* Keep language switcher and in-site Material chrome on the same tab. */
      if (
        anchor.classList.contains("md-select__link") ||
        anchor.closest(".md-nav") ||
        anchor.closest(".md-tabs")
      ) {
        return;
      }
      anchor.setAttribute("target", "_blank");
      var rel = (anchor.getAttribute("rel") || "").split(/\s+/).filter(Boolean);
      if (rel.indexOf("noopener") === -1) {
        rel.push("noopener");
      }
      if (rel.indexOf("noreferrer") === -1) {
        rel.push("noreferrer");
      }
      anchor.setAttribute("rel", rel.join(" "));
    });
  }

  function enhanceLanguage() {
    var path = location.pathname || "";
    document.querySelectorAll(".md-select__link[hreflang]").forEach(function (link) {
      var href = link.getAttribute("href") || "";
      var active =
        href === path ||
        (href.length > 1 && (path === href || path.indexOf(href.replace(/\/?$/, "/")) === 0));
      /* Prefer the longest matching locale prefix (e.g. /zh/ over /). */
      link.classList.toggle("md-select__link--active", false);
      link.dataset.hrefLen = String(href.length);
      if (active) {
        link.dataset.langCandidate = "1";
      } else {
        delete link.dataset.langCandidate;
      }
    });

    var best = null;
    var bestLen = -1;
    document.querySelectorAll('.md-select__link[data-lang-candidate="1"]').forEach(function (link) {
      var len = parseInt(link.dataset.hrefLen || "0", 10);
      if (len > bestLen) {
        best = link;
        bestLen = len;
      }
    });
    if (best) {
      best.classList.add("md-select__link--active");
    }
  }

  function enhance() {
    enhanceLinks(document);
    enhanceLanguage();
  }

  if (typeof document$ !== "undefined" && document$.subscribe) {
    document$.subscribe(enhance);
  } else if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", enhance);
  } else {
    enhance();
  }
})();
