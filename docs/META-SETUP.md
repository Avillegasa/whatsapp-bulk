# Configuración en Meta

Pasos que hace Andrés en los paneles de Meta y en su terminal. Cada paso dice dónde se hace y qué pegar de vuelta en Claude desktop.

> **Nunca pegues en el chat ni guardes en archivos:** el token, el App Secret, el verify token ni el PIN. Si un comando los muestra, tapa el valor con `***` antes de pegar.

## Identificadores (no secretos)

| Dato | Prueba (Fase 0) | Producción (Fase 1) |
| --- | --- | --- |
| Business portfolio ID | 1437472428502222 (WaBulk Preubas) | |
| App ID | 1669296924583840 (WaBulk PoC) | |
| WhatsApp account (WABA) ID | 2043065989689955 | |
| Messaging account ID (si aparece) | 2088365445132575 ("Identificador de la cuenta de mensajes" en Configuración de la API) | |
| Phone Number ID | 1524289747428097 | |
| Número visible | +1 (555) 189-2694 (prueba) | |
| System User (nombre) | wabulk-poc (ID 61596887810621) | |
| Graph API en uso | v27.0 | |

## Dónde viven los secretos

| Secreto | Desarrollo | Docker |
| --- | --- | --- |
| Token del System User | `dotnet user-secrets` → `Meta:AccessToken` | `META__ACCESSTOKEN` en `.env` |
| App Secret | `Meta:AppSecret` | `META__APPSECRET` |
| Verify token (lo inventas tú) | `Meta:VerifyToken` | `META__VERIFYTOKEN` |
| PIN de verificación en dos pasos | Gestor de contraseñas de la empresa | No lo usa la app |

---

## A. PoC con número de prueba (tarea F0.1)

1. 🖱 Manual — panel: entra a https://developers.facebook.com con la cuenta que administra el portafolio de la empresa. Si te lo pide, completa el registro de desarrollador.
   **Pega:** "listo" o el mensaje de error.
2. 🖱 Manual — panel: **My Apps → Create App** → caso de uso **Connect with customers through WhatsApp** → elige el portafolio de la empresa (no uno personal).
   **Pega:** el App ID.
3. 🖱 Manual — panel: en la app, **WhatsApp → API Setup**. Meta crea solo una cuenta de prueba y un número de prueba. En **To**, agrega tu número y confírmalo con el código que llega por WhatsApp (máximo 5 números).
   **Pega:** Phone Number ID y WhatsApp Business Account ID de esa pantalla.
4. 🖱 Manual — panel: en la misma pantalla, **Send message** con la plantilla `hello_world` a tu número.
   **Pega:** "llegó" o el error que muestre el panel.
5. 🖱 Manual — panel: **Business Settings → Users → System users → Add**, rol Admin. Luego **Assign assets**: la app, la WhatsApp account y, si aparece, la Messaging account. Luego **Generate token** con `business_management`, `whatsapp_business_management` y `whatsapp_business_messaging` (lista según https://developers.facebook.com/documentation/business-messaging/whatsapp/access-tokens, verificado 2026-11-16). En la PoC el panel no ofreció `business_management` (la app solo tiene el caso de uso de WhatsApp); el token se generó con los dos permisos de WhatsApp. Si una llamada falla por permisos, agregar ese caso de uso.
   **Pega:** nombre del System User y la lista de permisos que muestra el token (no el token).
6. 🖥 Terminal (cmd, desde la raíz del repo): guarda los secretos. El almacén de user-secrets es uno solo para Api, Webhooks y Worker (D-018). Token: hecho en F0.5. App Secret: en F0.6.
   ```cmd
   dotnet user-secrets set "Meta:AccessToken" "<token>" --project src\backend\WaBulk.Worker
   dotnet user-secrets set "Meta:AppSecret" "<app secret>" --project src\backend\WaBulk.Worker
   ```
   **Pega:** "token guardado" (comprobado con `dotnet user-secrets list ... | findstr /C:"Meta:AccessToken"`), nunca el valor.

## B. Webhooks en desarrollo (tarea F0.6)

Meta solo llama a URLs HTTPS públicas. En desarrollo se usa un túnel de Microsoft Dev Tunnels hacia el receptor local. El puerto del receptor lo fija F0.3 (aquí `5080` como ejemplo).

1. 🖥 Terminal (cmd): instala e inicia sesión.
   ```cmd
   winget install Microsoft.devtunnel
   devtunnel user login
   ```
   **Pega:** la última línea de cada comando.
2. 🖥 Terminal: crea un túnel persistente con un ID difícil de adivinar, abre el puerto y publícalo.
   ```cmd
   devtunnel create wabulk-<algo-aleatorio> -a
   devtunnel port create -p 5080 --protocol http
   devtunnel host
   ```
   **Pega:** la URL pública que muestra `devtunnel host` (formato `https://<id>-5080.<region>.devtunnels.ms/`).
   El túnel es anónimo: cualquiera que adivine el ID llega al receptor. Por eso la validación de firma es obligatoria y el túnel se apaga (Ctrl+C) cuando no lo usas.
3. 🖥 Terminal: guarda el verify token (una cadena que inventas, por ejemplo 32 caracteres aleatorios).
   ```cmd
   dotnet user-secrets set "Meta:VerifyToken" "<tu cadena>"
   ```
   **Pega:** "guardado".
4. 🖱 Manual — panel: **WhatsApp → Configuration → Webhook → Edit**. Callback URL: `<URL del túnel>/webhooks/whatsapp`. Verify token: la misma cadena. **Verify and save**.
   **Pega:** "verificado" o el error.
5. 🖱 Manual — panel: en **Webhook fields**, suscribe: `messages`, `message_template_status_update`, `message_template_quality_update`, `template_category_update`, `user_preferences`, `account_alerts`, `account_update`, `phone_number_quality_update`, `business_capability_update`, `security`.
   **Pega:** captura o lista de los campos suscritos.
6. 🖥 Terminal: suscribe la app a la cuenta de WhatsApp.
   ```cmd
   curl.exe -X POST "https://graph.facebook.com/v27.0/<WABA_ID>/subscribed_apps" -H "Authorization: Bearer <token>"
   ```
   **Pega:** la respuesta (debe ser `{"success":true}`).

## C. Producción (tarea F1.1, se detalla al llegar)

1. 🖱 Verificación del negocio en **Business Settings → Security Center**.
2. 🖱 Número dedicado de la empresa (no activo en la app de WhatsApp), nombre visible, verificación por SMS o llamada.
3. 🖥 Registro del número con `POST /{PHONE_NUMBER_ID}/register` y PIN de 6 dígitos.
4. 🖱 Método de pago en **Billing Hub**.
5. 🖱 Perfil de empresa del número.
6. 🖱 Asignar los activos reales al System User y repetir B.5–B.6 con la WABA real.
7. 🖥 Comprobar límite y throughput: `GET /{PHONE_NUMBER_ID}?fields=whatsapp_business_manager_messaging_limit,throughput,quality_rating`.

Referencias: https://developers.facebook.com/documentation/business-messaging/whatsapp/get-started · https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/cli-commands
