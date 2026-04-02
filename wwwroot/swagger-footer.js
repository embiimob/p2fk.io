// Appends a copyright footer to the Swagger UI page
(function () {
    function addFooter() {
        if (document.getElementById('p2fk-footer')) return;
        var footer = document.createElement('div');
        footer.id = 'p2fk-footer';
        footer.style.cssText = [
            'text-align:center',
            'padding:18px 0 24px',
            'font-size:0.82em',
            'color:#b0b0b0',
            'border-top:1px solid #2a2a2a',
            'margin-top:32px'
        ].join(';');
        footer.innerHTML =
            '\u00A9 2023\u20132026 Open-Source HugPuddle<br>' +
            '<a href="https://github.com/embiimob/p2fk.io" target="_blank" rel="noopener noreferrer" ' +
            'style="color:#03dac6;text-decoration:none;">' +
            'github.com/embiimob/p2fk.io</a>';
        document.body.appendChild(footer);
    }

    // Swagger UI renders asynchronously; poll until the main wrapper exists
    var attempts = 0;
    var interval = setInterval(function () {
        attempts++;
        var wrapper = document.querySelector('.swagger-ui');
        if (wrapper || attempts > 60) {
            clearInterval(interval);
            addFooter();
        }
    }, 500);
})();
