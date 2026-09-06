# Agency-Feature-Checkliste (manuell)

Stand: 06.09.2026. Ergänzt die automatisierten Tests (Gate-Checks + Happy-Path, siehe `Gentle.Book.API.Tests`) um das, was sich nicht sinnvoll automatisiert testen lässt — echte externe Aufrufe (DNS, KI, E-Mail-Zustellung).

**Voraussetzung:** Ein echter Test-Tenant auf dem Agency-Plan. Dafür gibt es keine fertige Seed-Fixture — am schnellsten über SuperAdmin: `/superadmin/tenants` → Tenant öffnen → Tab "Abo" → Plan auf "Agency" setzen (oder über den Angebots-Flow: Anfrage stellen, Angebot senden, annehmen).

## 1. Custom Domain (echte DNS-Verifikation)
- [ ] Im Kunden-Admin unter Einstellungen eine echte Domain eintragen (z. B. eine Subdomain, die man selbst kontrolliert)
- [ ] DNS-Eintrag beim Hoster wie in der Anleitung gesetzt
- [ ] SuperAdmin verifiziert die Domain manuell (kein automatisches Provisioning) und setzt den Status auf "Verified"
- [ ] Buchungsseite ist über die eigene Domain erreichbar, kein Redirect-Loop

## 2. KI-Tagline-Vorschläge (echter Live-Aufruf)
- [ ] Im Kunden-Admin bei einem Mitarbeiterprofil "Vorschlag anfragen" klicken
- [ ] Antwortqualität prüfen (3 kurze deutsche Wörter, passend zu Rolle/Spezialgebiet)
- [ ] Latenz/Kosten grob im Blick behalten (`GET /api/superadmin/ai-usage`)

## 3. Echte KI-Erklärungen im Service-Finder (echter Live-Aufruf)
- [ ] Öffentliche Buchungsseite eines Agency-Tenants → Service-Finder durchklicken
- [ ] Prüfen, dass eine echte, kontextbezogene Erklärung erscheint (nicht der deterministische Fallback-Text, der bei Nicht-Agency-Plänen kommt)

## 4. E-Mail-Versand bei den drei Einladungs-/Anfrage-Flows
- [ ] Bewertungsanfrage nach einem abgeschlossenen Termin — Kunde erhält die E-Mail, Link funktioniert
- [ ] Anamnesebogen-Einladung — Kunde erhält die E-Mail, Link funktioniert, Formular ist für die Branche des Tenants sichtbar
- [ ] Mehrstandort-Admin-Einladung — eingeladene Person erhält die Passwort-Setzen-E-Mail, kann sich einloggen und sieht nur ihren zugewiesenen Standort

## 5. Marken-Re-Analyse (echter Website-Fetch + KI-Aufruf)
- [ ] Bei einem bereits analysierten Tenant erneut "Analyse aktualisieren" anstoßen
- [ ] Prüfen, dass Änderungen auf der echten externen Website tatsächlich erkannt werden

## 6. API-Zugang (echter externer Client)
- [ ] Einen API-Key im Kunden-Admin erzeugen
- [ ] Mit einem echten HTTP-Client (curl/Postman) `GET /api/v1/services` mit `X-Api-Key` aufrufen und eine valide Antwort erhalten
- [ ] Denselben Aufruf mit einem Nicht-Agency-Tenant-Key wiederholen → erwartet 402

## Bereits automatisiert abgedeckt (nicht erneut manuell prüfen müssen)
Gate-Check + Happy-Path für alle 9 Features liegen in `Gentle.Book.API.Tests` (z. B. `PublicApiV1ControllerAgencyGateTests`, `TenantControllerCustomDomainTests`, `CustomersControllerLoyaltyTests`, `AdminVoucherControllerAgencyGateTests`, `AdminReviewsControllerAgencyGateTests`, `AdminIntakeFormControllerAgencyGateTests`, `EmployeesControllerAiTaglineTests`, `BrandImportPlanGateTests`, `TenantControllerLocationAdminInviteTests`) — die reine Zugriffslogik (Agency ja/nein, richtige Branche) muss hier nicht erneut von Hand geprüft werden.
