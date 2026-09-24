// Cambio di tabella nel mapping: si ricarica il form con le colonne della nuova tabella (nessuna scrittura).
document.querySelectorAll('select[data-refresh]').forEach(function (select) {
    select.addEventListener('change', function () {
        var form = select.form;
        form.querySelector('input[name="action"]').value = 'refresh';
        form.submit();
    });
});

// Feedback sulle operazioni lunghe (analisi del DB, verifica).
document.querySelectorAll('form[data-busy]').forEach(function (form) {
    form.addEventListener('submit', function () {
        var button = form.querySelector('button[type="submit"]');
        if (button) { button.disabled = true; button.textContent = form.getAttribute('data-busy'); }
    });
});

// Domande di esempio: riempiono la casella del messaggio.
document.querySelectorAll('[data-fill]').forEach(function (chip) {
    chip.addEventListener('click', function () {
        var box = document.querySelector('.composer textarea');
        if (box) { box.value = chip.getAttribute('data-fill'); box.focus(); }
    });
});
