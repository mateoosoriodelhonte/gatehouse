window.gatehouse = {
    copyText: async function (value) {
        await navigator.clipboard.writeText(value);
    },
    focus: function (id) {
        document.getElementById(id)?.focus();
    },
    setTheme: function (theme) {
        document.documentElement.dataset.theme = theme;
        localStorage.setItem("gatehouse-theme", theme);
    },
    initializeTheme: function () {
        const saved = localStorage.getItem("gatehouse-theme");
        document.documentElement.dataset.theme = saved === "light" ? "light" : "dark";
    }
};

window.gatehouse.initializeTheme();
