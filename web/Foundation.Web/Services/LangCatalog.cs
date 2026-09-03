namespace Foundation.Web.Services;

/// <summary>
/// Spanish for the driver-facing screens: the kiosk at the scale, the phone
/// page at /Mobile, the remote signature pad, and the ticket views those two
/// print. Office and admin screens are deliberately not in here yet — they stay
/// English until they are wrapped the same way.
///
/// Keys are the English source text exactly as it appears in the view, so a
/// string with no entry falls through to English rather than to a blank. Add a
/// line here and the string is translated everywhere it is used; there is no
/// resource compiler and no build step beyond a normal rebuild.
///
/// Values with {0} placeholders are filled by string.Format (server side) or
/// the page's TF() helper (browser side) — a translation may reorder them.
///
/// Master data is NOT translated. Customer, carrier, commodity, bin, location
/// and custom-field names come from the customer's own tables and print as
/// typed, in whatever language they were entered.
/// </summary>
public static class LangCatalog
{
    public static readonly IReadOnlyDictionary<string, string> Spanish =
        new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // ===== SHARED: field labels =====
        // These are the app's names for the standard ticket fields. The values
        // stored in them stay in the language the office typed them in.
        ["Carrier"] = "Transportista",
        ["Truck ID"] = "ID de Camión",
        ["Commodity"] = "Producto",
        ["Customer"] = "Cliente",
        ["Location"] = "Ubicación",
        ["Destination"] = "Destino",
        ["Bin"] = "Silo",
        ["Note"] = "Nota",
        ["Ticket"] = "Boleta",
        ["Gross"] = "Bruto",
        ["Tare"] = "Tara",
        ["Net"] = "Neto",
        ["Time In"] = "Hora de Entrada",
        ["Time Out"] = "Hora de Salida",
        ["Weighed In"] = "Peso de Entrada",
        ["In"] = "Entrada",
        ["Out"] = "Salida",
        ["Weigh In"] = "Pesaje de Entrada",
        ["Weigh Out"] = "Pesaje de Salida",
        ["Stored Tare"] = "Tara Guardada",
        ["Completed Ticket"] = "Boleta Completada",

        // ===== SHARED: buttons =====
        ["Cancel"] = "Cancelar",
        ["Skip"] = "Omitir",
        ["Select"] = "Seleccionar",
        ["Confirm"] = "Confirmar",
        ["Done"] = "Listo",
        ["Back"] = "Atrás",
        ["Reprint"] = "Reimprimir",
        ["OK"] = "Aceptar",
        ["Yes"] = "Sí",
        ["No"] = "No",
        ["CHANGE"] = "CAMBIAR",
        ["Change All"] = "Cambiar Todo",

        // ===== SHARED: prompt titles =====
        ["Select Item"] = "Seleccione un Elemento",
        ["Select Carrier"] = "Seleccione el Transportista",
        ["Select Truck"] = "Seleccione el Camión",
        ["Select Commodity"] = "Seleccione el Producto",
        ["Select Customer"] = "Seleccione el Cliente",
        ["Select Location"] = "Seleccione la Ubicación",
        ["Select Destination"] = "Seleccione el Destino",
        ["Select Bin"] = "Seleccione el Silo",
        // Custom fields: {0} is the admin-defined field name, left as typed.
        ["Select {0}"] = "Seleccione {0}",
        ["Enter {0}"] = "Ingrese {0}",
        // Bare placeholder titles, before a prompt fills the real one in.
        ["Enter"] = "Ingrese",
        ["— None —"] = "— Ninguno —",
        ["— none —"] = "— ninguno —",
        ["— skipped —"] = "— omitido —",

        // ===== SHARED: number keypad validation =====
        ["whole number"] = "número entero",
        ["min {0}"] = "mín {0}",
        ["max {0}"] = "máx {0}",
        ["up to {0} decimal"] = "hasta {0} decimal",
        ["up to {0} decimals"] = "hasta {0} decimales",
        ["A value is required"] = "Se requiere un valor",
        ["Not a valid number"] = "Número no válido",
        ["Minimum is {0}"] = "El mínimo es {0}",
        ["Maximum is {0}"] = "El máximo es {0}",
        ["Up to {0} decimal place"] = "Hasta {0} decimal",
        ["Up to {0} decimal places"] = "Hasta {0} decimales",

        // ===== KIOSK: chrome =====
        ["Kiosk"] = "Kiosco",

        // ===== KIOSK: weight display =====
        ["ERROR"] = "ERROR",
        ["MOTION"] = "EN MOVIMIENTO",
        ["Motion"] = "Movimiento",
        ["Error"] = "Error",

        // ===== KIOSK: idle / ready =====
        ["Place Truck on Scale"] = "Coloque el Camión en la Báscula",
        ["PRESENT YOUR CARD"] = "PRESENTE SU TARJETA",
        ["PRESS ENTER TO WEIGH IN"] = "PRESIONE ENTER PARA PESAR ENTRADA",
        ["SCAN BAR CODE OR KEY IN TICKET #"] = "ESCANEE EL CÓDIGO O TECLEE EL N.° DE BOLETA",
        ["PRESS"] = "PRESIONE",
        ["ENTER"] = "ENTER",
        ["TO WEIGH"] = "PARA PESAR",

        // ===== KIOSK: ticket entry =====
        ["Enter Ticket Number"] = "Ingrese el Número de Boleta",
        ["CLR"] = "BORR",
        ["ENT"] = "ENT",
        ["Checking Ticket"] = "Verificando la Boleta",
        ["Reading Card"] = "Leyendo la Tarjeta",

        // ===== KIOSK: motion wait =====
        ["Waiting for Scale"] = "Esperando la Báscula",
        ["Motion Detected — Please Wait"] = "Movimiento Detectado — Por Favor Espere",

        // ===== KIOSK: confirm =====
        ["Confirm & Weigh"] = "Confirmar y Pesar",
        ["Confirm New Load"] = "Confirmar Carga Nueva",
        ["Confirm Ticket"] = "Confirmar Boleta",
        ["Confirm Ticket #{0}"] = "Confirmar Boleta N.° {0}",
        ["PRESS ENTER TO CONFIRM — ESC TO CANCEL"] = "PRESIONE ENTER PARA CONFIRMAR — ESC PARA CANCELAR",

        // ===== KIOSK: saving / complete =====
        ["Saving"] = "Guardando",
        ["Complete"] = "Completado",
        ["WEIGH IN COMPLETE"] = "PESAJE DE ENTRADA COMPLETADO",
        ["WEIGH OUT COMPLETE"] = "PESAJE DE SALIDA COMPLETADO",
        ["PRESS ENTER FOR REPRINT"] = "PRESIONE ENTER PARA REIMPRIMIR",
        ["Ticket #{0}"] = "Boleta N.° {0}",
        ["{0} — Net: {1} lb"] = "{0} — Neto: {1} lb",
        ["{0} — {1} lb"] = "{0} — {1} lb",
        ["Tare recalled"] = "Tara recuperada",

        // ===== KIOSK: card guidance on the complete screen =====
        ["KEEP YOUR CARD — PRESENT IT AGAIN TO WEIGH OUT"] = "CONSERVE SU TARJETA — PRESÉNTELA DE NUEVO PARA PESAR SALIDA",
        ["KEEP YOUR CARD FOR YOUR NEXT LOAD"] = "CONSERVE SU TARJETA PARA SU PRÓXIMA CARGA",
        ["RETURN CARD TO THE LOADER OPERATOR"] = "DEVUELVA LA TARJETA AL OPERADOR DEL CARGADOR",

        // ===== KIOSK: overlays =====
        ["Scale Error"] = "Error de Báscula",
        ["Check scale connection"] = "Revise la conexión de la báscula",
        ["Operation Cancelled"] = "Operación Cancelada",
        ["Motion did not stop in time"] = "El movimiento no se detuvo a tiempo",
        ["Weight changed during operation"] = "El peso cambió durante la operación",
        ["Operation Timed Out"] = "Se Agotó el Tiempo de la Operación",
        ["Save Failed"] = "Error al Guardar",
        ["Server error. Please try again."] = "Error del servidor. Intente de nuevo.",
        ["Server error ({0}). Please try again."] = "Error del servidor ({0}). Intente de nuevo.",
        ["Ticket Not Found"] = "Boleta No Encontrada",
        ["Please try again"] = "Intente de nuevo",
        ["Ticket Already Completed"] = "Boleta Ya Completada",
        ["This ticket has already been weighed out"] = "Esta boleta ya pesó salida",
        ["Ticket Voided"] = "Boleta Anulada",
        ["This ticket has been voided"] = "Esta boleta fue anulada",
        ["No Trucks Assigned"] = "No Hay Camiones Asignados",
        ["No trucks are assigned to this carrier — see the office"] = "No hay camiones asignados a este transportista — consulte la oficina",
        ["No trucks are assigned to \"{0}\" — see the office"] = "No hay camiones asignados a \"{0}\" — consulte la oficina",
        // The phone shows this as a sentence in a banner, so it keeps its period.
        ["No trucks are assigned to \"{0}\" — see the office."] = "No hay camiones asignados a \"{0}\" — consulte la oficina.",
        ["Card"] = "Tarjeta",
        ["Then present your card again"] = "Luego presente su tarjeta de nuevo",
        ["Card lookup failed"] = "Falló la búsqueda de la tarjeta",

        // ===== KIOSK: no-printer warning =====
        ["No Printer Configured"] = "No Hay Impresora Configurada",
        ["This kiosk does not have a printer assigned. Tickets will {0}not{1} be printed."] = "Este kiosco no tiene impresora asignada. Las boletas {0}no{1} se imprimirán.",
        ["To configure printing, add printer parameters to the kiosk URL:"] = "Para configurar la impresión, agregue los parámetros de impresora a la URL del kiosco:",
        ["Use {0} to print via browser."] = "Use {0} para imprimir desde el navegador.",

        // ===== KIOSK: on-screen demo ticket =====
        ["TARE RECALLED:"] = "TARA RECUPERADA:",
        ["Ticket #:"] = "Boleta N.°:",
        ["Type:"] = "Tipo:",
        ["Commodity:"] = "Producto:",
        ["Customer:"] = "Cliente:",
        ["Carrier:"] = "Transportista:",
        ["Truck ID:"] = "ID de Camión:",
        ["Destination:"] = "Destino:",
        ["Bin:"] = "Silo:",
        ["Gross:"] = "Bruto:",
        ["Tare:"] = "Tara:",
        ["Net:"] = "Neto:",
        ["Weight:"] = "Peso:",
        [" (last seen {0})"] = " (visto por última vez {0})",

        // ===== MOBILE: chrome =====
        ["Weigh"] = "Pesaje",
        ["Connecting…"] = "Conectando…",
        ["Loading…"] = "Cargando…",
        ["Saving…"] = "Guardando…",
        ["Main dashboard"] = "Panel principal",
        ["Open the main dashboard"] = "Abrir el panel principal",
        ["Are you sure?"] = "¿Está seguro?",

        // ===== MOBILE: scale picker =====
        ["Choose Location"] = "Elija la Ubicación",
        ["Choose Scale"] = "Elija la Báscula",
        ["Scale at {0}"] = "Báscula en {0}",
        ["Scales"] = "Básculas",

        // ===== MOBILE: home / open ticket =====
        ["WEIGH IN"] = "PESAR ENTRADA",
        ["WEIGH OUT"] = "PESAR SALIDA",
        ["Pull onto the scale, then tap Weigh In."] = "Suba a la báscula y luego toque Pesar Entrada.",
        ["Ready. Tap Weigh In to start your load."] = "Listo. Toque Pesar Entrada para iniciar su carga.",
        ["Ready. Tap Weigh Out to close this load."] = "Listo. Toque Pesar Salida para cerrar esta carga.",
        ["Pull back onto the scale when your load is done."] = "Vuelva a subir a la báscula cuando termine su carga.",
        ["Pull back onto the scale, or close on this truck’s stored tare."] = "Vuelva a subir a la báscula, o cierre con la tara guardada de este camión.",
        ["Scale is not reporting — see the office."] = "La báscula no responde — consulte la oficina.",
        ["Reset — cancel this weigh-in"] = "Reiniciar — cancelar este pesaje de entrada",
        ["SCALE ERROR"] = "ERROR DE BÁSCULA",
        ["IN MOTION"] = "EN MOVIMIENTO",

        // ===== MOBILE: confirm =====
        ["Confirm Weigh In"] = "Confirmar Pesaje de Entrada",
        ["Confirm Weigh Out"] = "Confirmar Pesaje de Salida",
        ["Confirm Weigh Out · #{0}"] = "Confirmar Pesaje de Salida · N.° {0}",
        ["Confirm & Weigh In"] = "Confirmar y Pesar Entrada",
        ["Confirm & Weigh Out"] = "Confirmar y Pesar Salida",

        // ===== SHARED: on-scale end detectors =====
        // Shown when a detector at one end of the deck is blocked, meaning the
        // truck is hanging off the platform and the reading is not the whole
        // vehicle. Sites without detectors never see these.
        ["NOT ON SCALE"] = "NO ESTÁ EN LA BÁSCULA",
        ["The truck is not all the way on the scale"] = "El camión no está completamente en la báscula",
        ["The truck is not all the way on the scale — pull forward."] = "El camión no está completamente en la báscula — avance.",

        // Kiosk list states. "Loading" is used bare and gets its own ellipsis
        // appended by the page, so it needs an entry separate from "Loading…".
        ["Loading"] = "Cargando",
        ["Could not reach the server. Check the connection and try again."] = "No se pudo conectar con el servidor. Verifique la conexión e intente de nuevo.",

        // The simulated card reader in the kiosk header. Only shown when the
        // kiosk is mapped to the demo reader, but a Spanish site running a
        // demo should not meet English controls.
        ["Card #"] = "N.º de Tarjeta",
        ["Scan"] = "Escanear",

        // ===== KIOSK: retained tare =====
        // The two answers to the Stored Tare prompt. "Stored Tare" itself is a
        // shared prompt title above.
        ["Finish now — use stored {0} lb"] = "Terminar ahora — usar los {0} lb guardados",
        ["Re-tare this truck now"] = "Volver a tarar este camión ahora",
        [" (from {0})"] = " (del {0})",

        // Re-taring at the kiosk. It replaces the truck's stored empty weight
        // with what is on the scale right now and writes no ticket, so the
        // confirm screen is named for that and warns if the truck looks loaded.
        ["Confirm Re-tare"] = "Confirmar Nueva Tara",
        ["Check the truck is EMPTY — {0} lb is well above the stored tare of {1} lb."] = "Verifique que el camión esté VACÍO — {0} lb supera con creces la tara guardada de {1} lb.",
        // The phone asks this as its own step; the kiosk carries the same
        // warning on its confirm screen and needs no separate title.
        ["Is the truck empty?"] = "¿El camión está vacío?",
        ["Stored tare updated to {0} lb"] = "Tara guardada actualizada a {0} lb",
        ["No ticket was created."] = "No se creó ninguna boleta.",

        // ===== MOBILE: retained tare =====
        ["Reuse stored tare?"] = "¿Usar la tara guardada?",
        ["This truck has a stored empty weight of {0}. Use it to close the load, or weigh the truck on the scale now?"] = "Este camión tiene un peso vacío guardado de {0}. ¿Usarlo para cerrar la carga, o pesar el camión en la báscula ahora?",
        ["This truck has a stored empty weight of {0}. Use it to close the load, or weigh the truck on the scale now ({1} lb)?"] = "Este camión tiene un peso vacío guardado de {0}. ¿Usarlo para cerrar la carga, o pesar el camión en la báscula ahora ({1} lb)?",
        ["This truck has a stored empty weight of {0}. Finish the load now using it, and you will not need to weigh out. Re-taring instead replaces that weight with what is on the scale now, and writes no ticket — only do it with an empty truck."] = "Este camión tiene un peso vacío guardado de {0}. Termine la carga ahora usándolo y no necesitará pesar salida. Volver a tarar reemplaza ese peso con lo que hay en la báscula ahora y no crea ninguna boleta — hágalo solo con el camión vacío.",
        ["Use stored {0} lb"] = "Usar los {0} lb guardados",
        ["Weigh on scale"] = "Pesar en la báscula",
        ["Finish now"] = "Terminar ahora",
        // Shown instead of the two above when the site does not let phone
        // drivers reset a tare: declining then skips it for this load only.
        ["This truck has a stored empty weight of {0}. Finish the load now using it, and you will not need to weigh out. Otherwise the ticket stays open until you weigh out on the scale."] = "Este camión tiene un peso vacío guardado de {0}. Termine la carga ahora usándolo y no necesitará pesar salida. De lo contrario, la boleta queda abierta hasta que pese salida en la báscula.",
        ["Weigh out later"] = "Pesar salida después",
        ["Checking stored tare…"] = "Verificando la tara guardada…",
        ["{0} lb (from {1})"] = "{0} lb (del {1})",
        ["Closed on this truck’s stored empty weight."] = "Cerrada con el peso vacío guardado de este camión.",
        ["Weighed in and out on a retained tare."] = "Pesaje de entrada y salida con tara retenida.",

        // ===== MOBILE: weighment messages =====
        ["Scale is still moving — wait for it to settle."] = "La báscula sigue en movimiento — espere a que se estabilice.",
        ["Pull onto the scale before weighing."] = "Suba a la báscula antes de pesar.",
        ["Waiting for the scale to settle…"] = "Esperando a que la báscula se estabilice…",
        ["The truck left the scale before the weight was taken."] = "El camión salió de la báscula antes de tomar el peso.",
        ["Scale never settled. Hold still and try again."] = "La báscula nunca se estabilizó. No se mueva e intente de nuevo.",
        ["Weighed in — ticket #{0}"] = "Pesaje de entrada — boleta N.° {0}",
        ["This phone already has an open ticket."] = "Este teléfono ya tiene una boleta abierta.",
        ["That ticket is no longer open — see the office."] = "Esa boleta ya no está abierta — consulte la oficina.",
        ["Could not save ({0}). Try again."] = "No se pudo guardar ({0}). Intente de nuevo.",
        ["No scale is configured — see the office."] = "No hay báscula configurada — consulte la oficina.",
        ["Cannot reach the scale system."] = "No se puede conectar con el sistema de básculas.",
        ["Choose a carrier first."] = "Elija primero un transportista.",
        ["Could not load trucks. Try again."] = "No se pudieron cargar los camiones. Intente de nuevo.",
        ["No choices available for {0}."] = "No hay opciones disponibles para {0}.",

        // ===== MOBILE: reset =====
        ["Cancel this weigh-in?"] = "¿Cancelar este pesaje de entrada?",
        ["Ticket #{0} will be voided and this phone will start over. You cannot undo this — if the load is real, weigh out instead."] = "La boleta N.° {0} será anulada y este teléfono empezará de nuevo. Esto no se puede deshacer — si la carga es real, pese salida en su lugar.",
        ["Yes, void it"] = "Sí, anularla",
        ["Ticket #{0} voided."] = "Boleta N.° {0} anulada.",
        ["Session cleared."] = "Sesión borrada.",
        ["Could not reset. Try again."] = "No se pudo reiniciar. Intente de nuevo.",

        // ===== MOBILE: done screen =====
        ["Save Ticket as PDF"] = "Guardar Boleta como PDF",

        // ===== SERVER: kiosk + mobile API messages =====
        // Reach the driver through the same overlays and banners as the strings
        // above, so they are translated on the way out of the controller.
        ["Card weighing is turned off"] = "El pesaje con tarjeta está desactivado",
        ["No card number"] = "Sin número de tarjeta",
        ["Card Not Recognized"] = "Tarjeta No Reconocida",
        ["Card Disabled"] = "Tarjeta Desactivada",
        ["Card Not Active — See Loader Operator"] = "Tarjeta No Activa — Consulte al Operador del Cargador",
        ["Card not recognized."] = "Tarjeta no reconocida.",
        ["Card is not active — see the loader operator."] = "La tarjeta no está activa — consulte al operador del cargador.",
        ["Ticket not found"] = "Boleta no encontrada",
        ["Ticket is voided"] = "La boleta está anulada",
        ["Ticket already completed"] = "La boleta ya fue completada",
        ["No open ticket"] = "No hay boleta abierta",
        ["No open ticket on this phone."] = "No hay boleta abierta en este teléfono.",
        ["No scale configured."] = "No hay báscula configurada.",
        ["No truck on the scale — nothing to weigh in."] = "No hay camión en la báscula — nada que pesar de entrada.",
        ["No truck on the scale — nothing to weigh out."] = "No hay camión en la báscula — nada que pesar de salida.",
        ["That truck no longer has a stored empty weight. Weigh out on the scale."] = "Ese camión ya no tiene un peso vacío guardado. Pese salida en la báscula.",

        // ===== SIGNATURE PAD =====
        ["Signature Pad"] = "Tableta de Firma",
        ["Please wait…"] = "Por favor espere…",
        ["Sign here"] = "Firme aquí",
        ["Clear"] = "Borrar",
        ["Accept"] = "Aceptar",
        ["Thank you!"] = "¡Gracias!",
        ["Net {0} lb"] = "Neto {0} lb",
        ["Could not save the signature. Please try again."] = "No se pudo guardar la firma. Intente de nuevo.",
        // Connection status, bottom of the pad screen.
        ["connecting…"] = "conectando…",
        ["reconnecting…"] = "reconectando…",
        ["pad \"{0}\" • connected"] = "tableta \"{0}\" • conectada",
        ["disconnected — restart the page"] = "desconectado — reinicie la página",
        ["connection failed — retrying…"] = "falló la conexión — reintentando…",

        // ===== TICKET VIEWS =====
        // Page chrome and the browser-print ticket. The DevExpress .repx report
        // layouts the print agents render are customer-editable files and are
        // deliberately not touched here — a physical ticket keeps whatever
        // wording the site set up in the ticket designer.
        ["Ticket Preview"] = "Vista Previa de la Boleta",
        ["Kiosk Ticket"] = "Boleta del Kiosco",
        ["Back to Completed Trucks"] = "Volver a Camiones Completados",
        ["Print Ticket"] = "Imprimir Boleta",
        ["Inbound Ticket"] = "Boleta de Entrada",
        ["Inbound Photo"] = "Foto de Entrada",
        ["Outbound Photo"] = "Foto de Salida",
        ["Inbound — {0}"] = "Entrada — {0}",
        ["Outbound — {0}"] = "Salida — {0}",
        ["*** VOID ***"] = "*** ANULADA ***",
        ["Date In:"] = "Fecha de Entrada:",
        ["Date Out:"] = "Fecha de Salida:",
        ["Location:"] = "Ubicación:",
        ["Notes:"] = "Notas:",
        ["Gross Weight:"] = "Peso Bruto:",
        ["Tare Weight:"] = "Peso Tara:",
        ["Net Weight:"] = "Peso Neto:",

        // ===== LANGUAGE TOGGLE =====
        ["Switch to English"] = "Cambiar a inglés",
        ["Switch to Spanish"] = "Cambiar a español",
    };
}
