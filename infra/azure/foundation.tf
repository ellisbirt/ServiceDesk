locals {
  app_names = {
    helpdesk  = "${var.prefix}-helpdesk"
    agentdesk = "${var.prefix}-agentdesk"
  }
  tags = {
    application = "service-desk"
    environment = "poc"
    managed-by  = "terraform"
  }
}

resource "azurerm_resource_group" "poc" {
  name     = "${var.prefix}-poc"
  location = var.location
  tags     = local.tags
}

resource "azurerm_log_analytics_workspace" "poc" {
  name                = "${var.prefix}-logs"
  location            = azurerm_resource_group.poc.location
  resource_group_name = azurerm_resource_group.poc.name
  sku                 = "PerGB2018"
  retention_in_days   = 30
  daily_quota_gb      = 0.1
  tags                = local.tags
}

resource "azurerm_container_app_environment" "poc" {
  name                       = "${var.prefix}-apps"
  location                   = azurerm_resource_group.poc.location
  resource_group_name        = azurerm_resource_group.poc.name
  logs_destination           = "log-analytics"
  log_analytics_workspace_id = azurerm_log_analytics_workspace.poc.id
  tags                       = local.tags

  workload_profile {
    name                  = "Consumption"
    workload_profile_type = "Consumption"
  }
}

resource "azurerm_container_registry" "poc" {
  name                = "${var.prefix}acr"
  location            = azurerm_resource_group.poc.location
  resource_group_name = azurerm_resource_group.poc.name
  sku                 = "Basic"
  admin_enabled       = false
  tags                = local.tags
}

resource "azurerm_user_assigned_identity" "app" {
  for_each            = local.app_names
  name                = each.value
  location            = azurerm_resource_group.poc.location
  resource_group_name = azurerm_resource_group.poc.name
  tags                = local.tags
}

resource "azurerm_key_vault" "poc" {
  name                       = "${var.prefix}-kv"
  location                   = azurerm_resource_group.poc.location
  resource_group_name        = azurerm_resource_group.poc.name
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  rbac_authorization_enabled = true
  soft_delete_retention_days = 7
  tags                       = local.tags
}

resource "azurerm_role_assignment" "pull" {
  for_each             = azurerm_user_assigned_identity.app
  scope                = azurerm_container_registry.poc.id
  role_definition_name = "AcrPull"
  principal_id         = each.value.principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_role_assignment" "read_helpdesk_secrets" {
  scope                = azurerm_key_vault.poc.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_user_assigned_identity.app["helpdesk"].principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_role_assignment" "write_secrets" {
  scope                = azurerm_key_vault.poc.id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = var.secret_writer_principal_id
}

resource "azurerm_consumption_budget_resource_group" "poc" {
  count             = var.budget == null ? 0 : 1
  name              = "${var.prefix}-monthly"
  resource_group_id = azurerm_resource_group.poc.id
  amount            = var.budget.amount
  time_grain        = "Monthly"

  time_period {
    start_date = var.budget.start_date
  }

  notification {
    enabled        = true
    threshold      = 80
    operator       = "GreaterThanOrEqualTo"
    threshold_type = "Actual"
    contact_emails = [var.budget.email]
  }
}
