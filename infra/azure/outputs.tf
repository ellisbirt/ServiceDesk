output "resource_group" {
  value = azurerm_resource_group.poc.name
}

output "registry_name" {
  value = azurerm_container_registry.poc.name
}

output "registry_server" {
  value = azurerm_container_registry.poc.login_server
}

output "key_vault_name" {
  value = azurerm_key_vault.poc.name
}

output "app_urls" {
  description = "Reserved Azure-generated origins, available before the application deployment for Entra redirect registration."
  value       = { for key, host in local.app_hosts : key => "https://${host}" }
}

output "log_analytics_workspace_id" {
  value = azurerm_log_analytics_workspace.poc.workspace_id
}
