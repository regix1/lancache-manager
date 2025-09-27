# Test script to verify the Management API fixes

Write-Host "Testing Lancache Manager Management API Fixes" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host ""

# API base URL
$baseUrl = "http://localhost:5000/api"

Write-Host "1. Testing Process All Logs endpoint..." -ForegroundColor Yellow
try {
    $response = Invoke-RestMethod -Uri "$baseUrl/management/process-all-logs" -Method POST -ErrorAction Stop
    Write-Host "   ✓ Process All Logs endpoint is accessible" -ForegroundColor Green
    Write-Host "   Response: $($response | ConvertTo-Json -Compress)" -ForegroundColor Gray
} catch {
    Write-Host "   ✗ Process All Logs endpoint failed: $_" -ForegroundColor Red
}

Write-Host ""
Write-Host "2. Testing Cancel Processing endpoint..." -ForegroundColor Yellow
try {
    $response = Invoke-RestMethod -Uri "$baseUrl/management/cancel-processing" -Method POST -ErrorAction Stop
    Write-Host "   ✓ Cancel Processing endpoint is accessible" -ForegroundColor Green
    Write-Host "   Response: $($response | ConvertTo-Json -Compress)" -ForegroundColor Gray
} catch {
    Write-Host "   ✗ Cancel Processing endpoint failed: $_" -ForegroundColor Red
}

Write-Host ""
Write-Host "3. Testing Processing Status endpoint..." -ForegroundColor Yellow
try {
    $response = Invoke-RestMethod -Uri "$baseUrl/management/processing-status" -Method GET -ErrorAction Stop
    Write-Host "   ✓ Processing Status endpoint is accessible" -ForegroundColor Green
    Write-Host "   Response: $($response | ConvertTo-Json -Compress)" -ForegroundColor Gray
} catch {
    Write-Host "   ✗ Processing Status endpoint failed: $_" -ForegroundColor Red
}

Write-Host ""
Write-Host "4. Testing Post-Process Depot Mappings endpoint..." -ForegroundColor Yellow
try {
    $response = Invoke-RestMethod -Uri "$baseUrl/management/post-process-depot-mappings" -Method POST -ErrorAction Stop
    Write-Host "   ✓ Post-Process Depot Mappings endpoint is accessible" -ForegroundColor Green
    Write-Host "   Response: $($response | ConvertTo-Json -Compress)" -ForegroundColor Gray
} catch {
    Write-Host "   ✗ Post-Process Depot Mappings endpoint failed: $_" -ForegroundColor Red
}

Write-Host ""
Write-Host "5. Checking state.json for position persistence..." -ForegroundColor Yellow
$stateFile = "data/state.json"
if (Test-Path $stateFile) {
    $state = Get-Content $stateFile | ConvertFrom-Json
    $position = $state.LogProcessing.Position
    Write-Host "   ✓ State file exists" -ForegroundColor Green
    Write-Host "   Current log position: $position" -ForegroundColor Gray
    Write-Host "   Last updated: $($state.LogProcessing.LastUpdated)" -ForegroundColor Gray
} else {
    Write-Host "   ✗ State file not found at $stateFile" -ForegroundColor Red
}

Write-Host ""
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "Fix Summary:" -ForegroundColor Cyan
Write-Host ""
Write-Host "✓ Removed automatic depot processing trigger from LogWatcherService" -ForegroundColor Green
Write-Host "✓ Improved position saving frequency (5s bulk, 10s normal)" -ForegroundColor Green
Write-Host "✓ Added double-save on service stop for reliability" -ForegroundColor Green
Write-Host "✓ Implemented atomic file writes in StateService" -ForegroundColor Green
Write-Host "✓ Added manual 'Apply Depot Mappings' button in UI" -ForegroundColor Green
Write-Host "✓ Position is now properly preserved when cancelling" -ForegroundColor Green
Write-Host ""
Write-Host "Workflow:" -ForegroundColor Yellow
Write-Host "1. Click 'Process All Logs' to start log processing" -ForegroundColor White
Write-Host "2. Processing saves position every 5-10 seconds" -ForegroundColor White
Write-Host "3. If cancelled or app closed, position is saved" -ForegroundColor White
Write-Host "4. On resume, processing continues from saved position" -ForegroundColor White
Write-Host "5. After log processing completes, manually click 'Apply Depot Mappings'" -ForegroundColor White
Write-Host "6. This updates downloads with depot information without re-processing" -ForegroundColor White