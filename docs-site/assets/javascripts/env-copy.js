/* Add click-to-copy on the Variable column of configuration reference tables. */
(function () {
  var HEADERS = { Variable: true, "变量": true };
  var COPY_SVG =
    '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" aria-hidden="true">' +
    '<path d="M19 21H8V7h11m0-2H8a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h11a2 2 0 0 0 2-2V7a2 2 0 0 0-2-2m-3-4H4a2 2 0 0 0-2 2v14h2V3h12V1Z"/>' +
    "</svg>";
  var CHECK_SVG =
    '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" aria-hidden="true">' +
    '<path d="M21 7 9 19l-5.5-5.5 1.42-1.42L9 16.17 19.58 5.58 21 7Z"/>' +
    "</svg>";

  function zh() {
    return (document.documentElement.lang || "").toLowerCase().indexOf("zh") === 0;
  }

  function labels(done) {
    if (zh()) {
      return done ? "已复制" : "复制";
    }
    return done ? "Copied" : "Copy";
  }

  function isEnvTable(table) {
    var th = table.querySelector("thead th, tr th");
    return !!(th && HEADERS[th.textContent.trim()]);
  }

  function ensureScroll(table) {
    var parent = table.parentElement;
    if (!parent) {
      return;
    }
    if (
      parent.classList.contains("md-typeset__table") ||
      parent.classList.contains("env-var-scroll")
    ) {
      return;
    }
    var wrap = document.createElement("div");
    wrap.className = "env-var-scroll";
    parent.insertBefore(wrap, table);
    wrap.appendChild(table);
  }

  function enhanceTable(table) {
    if (table.dataset.envCopy === "1" || !isEnvTable(table)) {
      return;
    }
    table.dataset.envCopy = "1";
    table.classList.add("env-var-table");
    ensureScroll(table);

    table.querySelectorAll("tbody tr").forEach(function (row) {
      var cell = row.cells[0];
      if (!cell) {
        return;
      }
      var code = cell.querySelector(":scope > code, :scope > .env-var > code");
      if (!code) {
        code = cell.querySelector("code");
      }
      if (!code || cell.querySelector(".env-var-copy")) {
        return;
      }

      var name = (code.textContent || "").trim();
      if (!name) {
        return;
      }

      var wrap = document.createElement("span");
      wrap.className = "env-var";
      code.replaceWith(wrap);
      wrap.appendChild(code);

      var btn = document.createElement("button");
      btn.type = "button";
      btn.className = "env-var-copy";
      btn.title = labels(false);
      btn.setAttribute("aria-label", labels(false) + " " + name);
      btn.innerHTML = COPY_SVG;

      btn.addEventListener("click", function () {
        var write =
          navigator.clipboard && navigator.clipboard.writeText
            ? navigator.clipboard.writeText(name)
            : Promise.reject();

        write
          .catch(function () {
            var area = document.createElement("textarea");
            area.value = name;
            area.setAttribute("readonly", "");
            area.style.position = "fixed";
            area.style.opacity = "0";
            document.body.appendChild(area);
            area.select();
            document.execCommand("copy");
            document.body.removeChild(area);
          })
          .then(function () {
            btn.classList.add("env-var-copy--done");
            btn.title = labels(true);
            btn.setAttribute("aria-label", labels(true) + " " + name);
            btn.innerHTML = CHECK_SVG;
            window.setTimeout(function () {
              btn.classList.remove("env-var-copy--done");
              btn.title = labels(false);
              btn.setAttribute("aria-label", labels(false) + " " + name);
              btn.innerHTML = COPY_SVG;
            }, 1500);
          });
      });

      wrap.appendChild(btn);
    });
  }

  function enhance() {
    document.querySelectorAll(".md-typeset table").forEach(enhanceTable);
  }

  if (typeof document$ !== "undefined" && document$.subscribe) {
    document$.subscribe(enhance);
  } else if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", enhance);
  } else {
    enhance();
  }
})();
