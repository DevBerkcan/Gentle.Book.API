# GentleBook API — Deployment auf MonsterASP

## Umgebungen

Production und Staging müssen als zwei getrennte Anwendungen mit getrennten
Datenbanken betrieben werden. Für Staging wird
`ASPNETCORE_ENVIRONMENT=Staging` gesetzt; die nicht geheimen Defaults stehen in
`appsettings.Staging.json`. Connection String, JWT-Secrets, E-Mail-, Mollie- und
CRM-Zugangsdaten werden ausschließlich als Hosting-Umgebungsvariablen gesetzt.

Die vollständige Zuordnung zwischen Vercel-Frontend, API und Datenbank ist in
`../Gentle.Book.UI/DEPLOYMENT_ENVIRONMENTS.md` dokumentiert.

## Voraussetzungen
- MonsterASP Account mit .NET 8 Hosting-Paket
- SQL Server-Datenbank auf MonsterASP (NEUE, leere DB — NICHT die Skinbloom-DB!)
- GentleBook.Api.csproj als Deployment-Target

---

## Schritt 1: Neue SQL Server Datenbank anlegen

Im MonsterASP Control Panel:
1. Databases → SQL Server → "Create Database"
2. Name: `gentlebook_prod` (oder dein gewünschter Name)
3. Zugangsdaten notieren (Server, DB-Name, User, Passwort)

**WICHTIG: Dies ist eine komplett neue DB — nicht die Skinbloom-DB!**

---

## Schritt 2: appsettings.Production.json erstellen

Erstelle lokal diese Datei (NIEMALS committen — steht in .gitignore):

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=YOUR_MONSTERASP_SQLSERVER;Database=gentlebook_prod;User Id=YOUR_DB_USER;Password=YOUR_DB_PASSWORD;TrustServerCertificate=true;MultipleActiveResultSets=true"
  },
  "Platform": {
    "Name": "GentleBook",
    "SupportEmail": "deine@email.de",
    "DefaultTrialDays": 14
  },
  "Jwt": {
    "Secret": "GENERATE_64_CHAR_RANDOM_STRING_HERE",
    "Issuer": "gentlebook-api",
    "Audience": "gentlebook-client",
    "ExpiryHours": 8,
    "SuperAdminSecret": "GENERATE_ANOTHER_64_CHAR_RANDOM_STRING_HERE",
    "SuperAdminIssuer": "gentlebook-superadmin",
    "SuperAdminAudience": "gentlebook-superadmin-client"
  },
  "AdminBootstrapSecret": "GENERATE_SECURE_RANDOM_STRING_FOR_BOOTSTRAP",
  "Email": {
    "SmtpServer": "smtp.ionos.de",
    "SmtpPort": 587,
    "SmtpUsername": "DEINE_EMAIL@domain.de",
    "SmtpPassword": "DEIN_SMTP_PASSWORT",
    "SenderEmail": "noreply@deinedomain.de",
    "SenderName": "GentleBook",
    "BaseUrl": "https://DEINE_FRONTEND_URL.vercel.app"
  },
  "Cors": {
    "AllowedOrigins": [
      "https://DEINE_FRONTEND_URL.vercel.app",
      "https://deinedomain.de"
    ]
  }
}
```

**Secrets generieren (in Terminal):**
```bash
# Für JWT Secret:
node -e "console.log(require('crypto').randomBytes(64).toString('hex'))"

# Für Bootstrap Secret:
node -e "console.log(require('crypto').randomBytes(32).toString('hex'))"
```

---

## Schritt 3: Projekt publishen

**WICHTIG — `-r win-x86` ist Pflicht, nicht optional.** Der MonsterASP-IIS-App-Pool für
site62449 läuft als 32-Bit-Prozess. Ein mit `-r win-x64` (oder ganz ohne `-r`) gebautes
Deployment lädt dort **überhaupt nicht** — nicht mal eine Zeile eigener Code läuft an, keine
Exception, kein Log, einfach dauerhaft HTTP 500.30. Das hat am 2026-07-31 einen mehrstündigen
Ausfall verursacht, bis der Bitness-Mismatch gefunden wurde (verräterisches Zeichen:
`Microsoft.Data.SqlClient.SNI.dll` im aktuell laufenden `wwwroot` ist exakt 414248 Byte groß —
das ist die x86-Variante; die x64-Variante hat eine andere Dateigröße. Vor jedem Deploy zur
Sicherheit vergleichen.)

```bash
dotnet publish GentleBook.Api.csproj \
  -c Release \
  -r win-x86 \
  --self-contained false \
  -o ./publish
```

---

## Schritt 4: Upload zu MonsterASP

1. Via FTP/FileZilla: alle Dateien aus `./publish` → `public_html/` (oder dein API-Verzeichnis)
2. ODER: Via MonsterASP Git Deploy (empfohlen)
3. `appsettings.Production.json` separat hochladen (NICHT über Git)

**web.config** wird automatisch generiert. Falls nötig, manuell anpassen:
```xml
<aspNetCore processPath="dotnet" arguments=".\GentleBook.Api.dll" 
            stdoutLogEnabled="false" stdoutLogFile=".\logs\stdout" 
            hostingModel="inprocess">
  <environmentVariables>
    <environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Production" />
  </environmentVariables>
</aspNetCore>
```

---

## Schritt 5: Datenbank-Migration ausführen

Nach dem ersten Deployment, in der MonsterASP-Konsole oder lokal gegen die Prod-DB:

```bash
# Lokal gegen Prod-DB:
$env:ASPNETCORE_ENVIRONMENT="Production"
dotnet ef database update --project GentleBook.Api.csproj
```

---

## Schritt 6: Ersten SuperAdmin anlegen (einmalig!)

```bash
curl -X POST https://DEINE_API_URL/api/auth/superadmin/bootstrap \
  -H "Content-Type: application/json" \
  -H "X-Bootstrap-Secret: DEIN_ADMIN_BOOTSTRAP_SECRET" \
  -d '{"email":"admin@deinedomain.de","password":"SICHERES_PASSWORT","firstName":"Super","lastName":"Admin"}'
```

**Danach:** `AdminBootstrapSecret` aus der Config entfernen oder auf einen zufälligen Wert setzen — Bootstrap ist einmalig!

---

## Checkliste vor Go-Live

- [ ] Neue (leere) Datenbank auf MonsterASP angelegt
- [ ] appsettings.Production.json mit echten Werten befüllt (lokal, nicht committet)
- [ ] JWT Secrets neu generiert (nicht wiederverwendet)
- [ ] CORS AllowedOrigins auf echte Frontend-URL gesetzt
- [ ] Deployment erfolgreich
- [ ] db.Database.MigrateAsync() hat alle Tabellen angelegt
- [ ] SuperAdmin via Bootstrap-Endpoint angelegt
- [ ] Bootstrap-Endpoint deaktiviert (Secret rotiert)
- [ ] /health erreichbar: `GET https://DEINE_API_URL/health`
- [ ] Swagger erreichbar: `GET https://DEINE_API_URL/swagger` (nur Development!)
