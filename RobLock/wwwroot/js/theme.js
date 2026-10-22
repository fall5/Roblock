// Night / Day theme. Loaded in <head> so the saved theme is applied before the page paints.
(function () {
  var KEY = "roblock_theme";

  function read() {
    try { return localStorage.getItem(KEY); } catch (e) { return null; }
  }
  function write(value) {
    try { localStorage.setItem(KEY, value); } catch (e) { /* storage blocked: theme just won't persist */ }
  }

  var theme = read() === "light" ? "light" : "dark";
  document.documentElement.setAttribute("data-theme", theme);

  window.RobTheme = {
    toggle: function () {
      theme = theme === "light" ? "dark" : "light";
      document.documentElement.setAttribute("data-theme", theme);
      write(theme);
    }
  };
})();
