terraform {
  required_version = ">= 1.7.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 3.110"
    }
    azuread = {
      source  = "hashicorp/azuread"
      version = "~> 2.53"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }

  # Remote State empfohlen (siehe scripts/tf-init-backend.sh, das die Storage
  # Account/Container-Voraussetzung dafür anlegt). Für den allerersten lokalen
  # Testlauf kann dieser Block auskommentiert bleiben (lokaler State), sollte
  # aber vor dem produktiven Einsatz aktiviert werden.
  backend "azurerm" {
    resource_group_name  = "rg-tfstate"
    storage_account_name = "stgeuwmcpo365dev"
    container_name       = "tfstate"
    key                  = "entra-mcp-mvp.tfstate"
  }
}

provider "azurerm" {
  features {
    key_vault {
      purge_soft_delete_on_destroy    = false
      recover_soft_deleted_key_vaults = true
    }
  }
}

provider "azuread" {}
