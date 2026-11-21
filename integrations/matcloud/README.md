# ThixShot in ThixCloud freischalten

ThixShot koppelt ein eigenes Gerät mit der App-ID `shot` über die Browseranmeldung von MatCloud. Die App legt im persönlichen ThixDrive den Ordner **ThixShot** an. Gleichnamige Uploads erhalten einen freien Namen. Große Aufnahmen werden in Teilen übertragen.

MatCloud-Vertrag 1.4.0 kennt die App-ID noch nicht. Der beiliegende Patch erweitert die App-Liste und die Gastfreigabe des Drive-Moduls und aktualisiert die zugehörigen Berechtigungstests. Im MatCloud-Checkout mit `ThixShot` als Nachbarverzeichnis:

```sh
git apply --check ../ThixShot/integrations/matcloud/thixshot.patch
git apply ../ThixShot/integrations/matcloud/thixshot.patch
dotnet test
```

Die geänderte MatCloud regulär bereitstellen und im Administrationsbereich ThixShot und ThixDrive aktivieren. Unter **ThixCloud** die HTTPS-Adresse der Installation angeben und **Im Browser anmelden** wählen. Den Gerätecode bestätigen und eine Aufnahme in der Mediathek für den Upload auswählen. Der Gerätezugang lässt sich im ThixCloud-Konto widerrufen.

Der Desktop-Client enthält eine mit MatClouds `tools/contract-copy.js` erzeugte Kopie des gepatchten Vertrags 1.4.1. Die Speicherberechtigung und das Kontingent prüft der Server bei jedem Upload.
