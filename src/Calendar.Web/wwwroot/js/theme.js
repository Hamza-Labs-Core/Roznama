// Theme persistence for the app shell. Light/dark follows the OS by default; an explicit user choice
// (the ThemeToggle) is stored in localStorage under "uc-theme" and pins the `data-theme` attribute.
window.ucTheme = {
    // The theme actually in effect: a pinned choice, else the OS preference.
    effective: function () {
        var stored = localStorage.getItem("uc-theme");
        if (stored === "dark" || stored === "light") return stored;
        return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
    },

    // Pin a theme and reflect it on <html data-theme>.
    set: function (theme) {
        if (theme === "dark" || theme === "light") {
            localStorage.setItem("uc-theme", theme);
            document.documentElement.setAttribute("data-theme", theme);
        }
        return window.ucTheme.effective();
    },

    // Flip between light and dark, returning the new effective theme.
    toggle: function () {
        var next = window.ucTheme.effective() === "dark" ? "light" : "dark";
        return window.ucTheme.set(next);
    }
};
