ALTER TABLE hm_domains ADD domainmessageretentiondays int not null DEFAULT 0

ALTER TABLE hm_accounts ADD accountmessageretentiondays int not null DEFAULT 0

update hm_dbversion set value = 6027
