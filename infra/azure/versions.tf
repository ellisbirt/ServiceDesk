terraform {
  required_version = ">= 1.10, < 2.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.57.0"
    }
  }

  backend "azurerm" {
    use_azuread_auth = true
  }
}

provider "azurerm" {
  features {
    storage {
      data_plane_available = false
    }
  }
  subscription_id = var.subscription_id
}

data "azurerm_client_config" "current" {}
