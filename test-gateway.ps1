$baseUrl = "http://localhost:5055"

Invoke-RestMethod "$baseUrl/api/health/database" |
    ConvertTo-Json -Depth 5

$result = Invoke-RestMethod "$baseUrl/api/reorder-list"
Write-Host ("Articoli restituiti: {0}" -f $result.count)

$result.items |
    Select-Object -First 10 |
    Format-Table idArticle, articleCode, description, stock, minimumStock, reorderLot
