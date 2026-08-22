variable "environment_name" {
  type = string
}

variable "mcp_redirect_uri" {
  type = string
}

variable "mcp_redirect_uris" {
  type    = list(string)
  default = null
}

variable "api_swagger_redirect_uris" {
  type = list(string)
}

variable "chatgpt_mcp_redirect_uris" {
  type    = list(string)
  default = []
}

variable "claude_mcp_redirect_uris" {
  type    = list(string)
  default = []
}

variable "copilot_mcp_redirect_uris" {
  type    = list(string)
  default = []
}
