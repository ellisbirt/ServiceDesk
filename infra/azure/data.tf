resource "azurerm_storage_account" "poc" {
  name                            = "${var.prefix}data"
  location                        = azurerm_resource_group.poc.location
  resource_group_name             = azurerm_resource_group.poc.name
  account_tier                    = "Standard"
  account_replication_type        = "LRS"
  min_tls_version                 = "TLS1_2"
  shared_access_key_enabled       = false
  allow_nested_items_to_be_public = false
  tags                            = local.tags

  blob_properties {
    cors_rule {
      allowed_origins    = ["https://${local.app_names.agentdesk}.${azurerm_container_app_environment.poc.default_domain}"]
      allowed_methods    = ["GET", "HEAD", "PUT", "OPTIONS"]
      allowed_headers    = ["content-type", "x-ms-blob-type"]
      exposed_headers    = ["ETag", "x-ms-request-id"]
      max_age_in_seconds = 300
    }
  }
}

resource "azurerm_storage_container" "attachments" {
  name                  = "ticket-attachments"
  storage_account_id    = azurerm_storage_account.poc.id
  container_access_type = "private"
}

resource "azurerm_storage_queue" "intake" {
  for_each           = toset(["private-corp-tickets", "private-corp-tickets-poison"])
  name               = each.value
  storage_account_id = azurerm_storage_account.poc.id
}

resource "azurerm_role_assignment" "attachments" {
  for_each             = azurerm_user_assigned_identity.app
  scope                = azurerm_storage_container.attachments.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = each.value.principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_role_assignment" "delegate_blob_sas" {
  scope                = azurerm_storage_account.poc.id
  role_definition_name = "Storage Blob Delegator"
  principal_id         = azurerm_user_assigned_identity.app["agentdesk"].principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_role_assignment" "helpdesk_queue" {
  scope                = azurerm_storage_queue.intake["private-corp-tickets"].id
  role_definition_name = "Storage Queue Data Contributor"
  principal_id         = azurerm_user_assigned_identity.app["helpdesk"].principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_role_assignment" "agentdesk_queues" {
  for_each             = azurerm_storage_queue.intake
  scope                = each.value.id
  role_definition_name = "Storage Queue Data Contributor"
  principal_id         = azurerm_user_assigned_identity.app["agentdesk"].principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_cosmosdb_account" "poc" {
  name                          = "${var.prefix}-cosmos"
  location                      = azurerm_resource_group.poc.location
  resource_group_name           = azurerm_resource_group.poc.name
  offer_type                    = "Standard"
  kind                          = "GlobalDocumentDB"
  local_authentication_disabled = true
  tags                          = local.tags

  capabilities {
    name = "EnableServerless"
  }

  consistency_policy {
    consistency_level = "Session"
  }

  geo_location {
    location          = azurerm_resource_group.poc.location
    failover_priority = 0
  }
}

resource "azurerm_cosmosdb_sql_database" "agentdesk" {
  name                = "agentdesk"
  resource_group_name = azurerm_resource_group.poc.name
  account_name        = azurerm_cosmosdb_account.poc.name
}

resource "azurerm_cosmosdb_sql_container" "agentdesk" {
  for_each            = toset(["tickets", "kb", "attachments"])
  name                = each.key
  resource_group_name = azurerm_resource_group.poc.name
  account_name        = azurerm_cosmosdb_account.poc.name
  database_name       = azurerm_cosmosdb_sql_database.agentdesk.name
  partition_key_paths = ["/tenantId"]
  default_ttl         = each.key == "tickets" ? -1 : null

  indexing_policy {
    indexing_mode = "consistent"

    included_path {
      path = "/*"
    }

    dynamic "excluded_path" {
      for_each = each.key == "kb" ? ["/embedding/*"] : []
      content {
        path = excluded_path.value
      }
    }
  }
}

resource "azurerm_cosmosdb_sql_role_assignment" "agentdesk" {
  resource_group_name = azurerm_resource_group.poc.name
  account_name        = azurerm_cosmosdb_account.poc.name
  role_definition_id  = "${azurerm_cosmosdb_account.poc.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002"
  principal_id        = azurerm_user_assigned_identity.app["agentdesk"].principal_id
  scope               = azurerm_cosmosdb_account.poc.id
}
