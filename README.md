# Scan2Enter Gateway

Gateway ASP.NET Core in sola lettura per ottenere dal database Due la lista
degli articoli sotto scorta.

## Regola verificata

```sql
ScortaMinima IS NOT NULL
AND Giacenza <= ScortaMinima
```

Il lotto di riordino non filtra la lista: può essere positivo, zero o NULL.

## Prima configurazione

1. Installare .NET 8 SDK.
2. Aprire `appsettings.json`.
3. Sostituire `INSERISCI_PASSWORD` con la password SQL dell'utente `sa`.

## Avvio

```powershell
dotnet restore
dotnet run
```

Il servizio ascolta su:

```text
http://localhost:5055
```

## Controlli

```text
http://localhost:5055/api/health/database
http://localhost:5055/api/reorder-list
```

Oppure:

```powershell
.\test-gateway.ps1
```

Il campo `count` dovrebbe restituire 121 finché i dati coincidono con
l'esportazione verificata in Due.

## Accesso dal Samsung

Usare l'IPv4 del PC, per esempio:

```text
http://192.168.1.30:5055/api/reorder-list
```

Se non risponde, aprire la porta TCP 5055 nel firewall di Windows.

## Sicurezza

Questa versione:
- esegue solo SELECT;
- non modifica il database;
- non espone endpoint di scrittura;
- è pensata per la rete locale.
