ALTER TABLE hm_domains ADD domaindkimsecondaryselector nvarchar(255) NOT NULL DEFAULT '' --- [IGNORE-ERRORS]

ALTER TABLE hm_domains ADD domaindkimsecondaryprivatekeyfile nvarchar(255) NOT NULL DEFAULT '' --- [IGNORE-ERRORS]

update hm_dbversion set value = 6007
